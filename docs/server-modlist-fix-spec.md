# 服务端 ModList 多客户端同步修复方案

> 基础路径：`/api/sync-config`  
> 认证方式：`Authorization: Bearer {token}`

---

## 问题背景

当两台客户端同时工作时，存在以下核心问题：

1. **回声问题**：客户端 A 上传文件后，下次 modlist 轮询返回了自己刚上传的变更，导致 A 把自己的新文件回退到旧版本（数据丢失）
2. **来源缺失**：所有写操作（upload/delete/rename）没有记录是哪个客户端发起的，modlist 无法区分"自己的变更"和"别人的变更"
3. **下载不一致**：upload 返回 200 成功但 modlist 返回旧数据，download 也返回旧数据，说明服务端存在数据一致性问题

---

## 改动总览

| 改动项 | 涉及接口 | 说明 |
|--------|----------|------|
| 1. 写操作携带 client_id | upload-stream, delete, rename | 记录每个变更的来源客户端 |
| 2. modlist 接受 client_id 参数 | modlist | 排除自己发起的变更，只返回其他客户端的变化 |
| 3. modlist 返回磁盘实时数据 | modlist | mtime 和 size 取当前磁盘文件的真实值，不返回缓存/历史值 |

---

## 1. 变更记录表设计

需要一张 `file_changes` 表（或等效内存结构），记录每次文件变更：

| 字段 | 类型 | 说明 |
|------|------|------|
| `id` | BIGINT AUTO_INCREMENT PK | 自增变更序号 |
| `path` | VARCHAR(1024) | 文件相对路径 |
| `action` | VARCHAR(16) | `update` 或 `delete` |
| `client_id` | VARCHAR(36) | 发起变更的客户端 UUID |
| `mtime` | BIGINT | 变更时的 mtime（Unix 秒） |
| `size` | BIGINT | 变更时的文件大小 |
| `created_at` | DATETIME | 记录时间 |

同时需要一张 `client_cursors` 表，记录每个客户端已消费的变更位置：

| 字段 | 类型 | 说明 |
|------|------|------|
| `client_id` | VARCHAR(36) PK | 客户端 UUID |
| `last_change_id` | BIGINT | 该客户端已消费的最大变更 ID |

> **如果你当前 modlist 已经有类似机制**（比如用文件 mtime 变化判断），只需要在此基础上加上 `client_id` 字段即可。核心要求是：**能按 client_id 过滤**。

---

## 2. 写操作改动：携带 client_id

### 2.1 PUT `/upload-stream` — 上传文件

**改动**：新增 `client_id` query 参数。

```
现在：PUT /api/sync-config/upload-stream?path=abc.txt&mtime=1772487734&offset=0
改后：PUT /api/sync-config/upload-stream?path=abc.txt&mtime=1772487734&offset=0&client_id=f73a397a-c683-440a-b818-6325e86b3935
```

服务端处理：
1. 正常保存文件（逻辑不变）
2. 写入 `file_changes` 记录，记下 `client_id`
3. **确保文件写入磁盘成功后再返回 200**（避免 upload 返回成功但文件内容未持久化）

### 2.2 POST `/delete` — 删除文件

**改动**：请求体新增 `client_id` 字段。

```json
{
    "path": "abc.txt",
    "client_id": "f73a397a-c683-440a-b818-6325e86b3935"
}
```

服务端处理：
1. 正常删除文件
2. 写入 `file_changes` 记录（action=delete），记下 `client_id`

### 2.3 POST `/rename` — 重命名

**改动**：请求体新增 `client_id` 字段。

```json
{
    "old_path": "新建 文本文档.txt",
    "new_path": "hhz.txt",
    "client_id": "f73a397a-c683-440a-b818-6325e86b3935"
}
```

服务端处理：
1. 正常重命名文件
2. 写入**两条** `file_changes` 记录：
   - `{ path: "新建 文本文档.txt", action: "delete", client_id: "..." }`（旧路径标记为删除）
   - `{ path: "hhz.txt", action: "update", client_id: "..." }`（新路径标记为更新）

> 因为客户端的 ModList 目前不理解 rename 操作，拆成 delete + update 是正确的。

### 2.4 向下兼容

如果请求中没有 `client_id`，可以用空字符串 `""` 或 `"unknown"` 作为默认值。不要报错。

---

## 3. GET `/modlist` 改动（核心）

### 3.1 新增参数

```
现在：GET /api/sync-config/modlist
改后：GET /api/sync-config/modlist?client_id=f73a397a-c683-440a-b818-6325e86b3935
```

### 3.2 业务逻辑（伪代码）

```python
def get_modlist(client_id):
    # 1. 获取该客户端的游标（上次消费到的位置）
    cursor = get_cursor(client_id)  # 没有记录就从 0 开始
    
    # 2. 查询该游标之后的所有变更，排除自己发起的
    changes = query("""
        SELECT * FROM file_changes 
        WHERE id > cursor 
        AND client_id != client_id_param   -- ← 关键：排除自己的变更
        ORDER BY id ASC
    """)
    
    # 3. 对同一路径的多条变更，只保留最后一条
    latest = {}
    max_id = cursor
    for change in changes:
        latest[change.path] = change
        max_id = max(max_id, change.id)
    
    # 4. 对 action=update 的条目，从磁盘获取实时 mtime 和 size
    result = []
    for path, change in latest.items():
        if change.action == "delete":
            result.append({ "path": path, "action": "delete", "mtime": 0, "size": 0 })
        else:  # update
            real_stat = os.stat(real_file_path(path))      # ← 关键：读磁盘真实值
            result.append({
                "path": path,
                "action": "update",
                "mtime": int(real_stat.st_mtime),           # 磁盘实时 mtime
                "size": real_stat.st_size                    # 磁盘实时 size
            })
    
    # 5. 更新游标（包含自己的变更 ID 也要跳过）
    all_max_id = query("SELECT MAX(id) FROM file_changes WHERE id > cursor")
    update_cursor(client_id, all_max_id)                    # ← 关键：游标推进包含自己的变更
    
    return { "items": result }
```

### 3.3 关键要点

| 要点 | 说明 |
|------|------|
| **排除自己的变更** | `client_id != 请求中的 client_id`，这样客户端 A 上传后不会收到自己的变更通知 |
| **游标推进包含所有变更** | 虽然返回时排除了自己的变更，但游标要推进到**所有变更**的最大 ID（包括自己的）。否则下次轮询又会查到自己的旧变更 |
| **从磁盘读实际值** | update 类型的条目，mtime 和 size 必须从磁盘读取真实值，不要用 `file_changes` 表中缓存的值。因为文件可能在记录创建后又被其他客户端修改了 |
| **文件已不存在** | 对 update 条目，如果读磁盘发现文件已被删除，改为返回 `action: "delete"` |

### 3.4 响应格式（不变）

```json
{
    "items": [
        {
            "path": "hhz.txt",
            "action": "update",
            "mtime": 1772488150,
            "size": 14
        },
        {
            "path": "新建 文本文档.txt",
            "action": "delete",
            "mtime": 0,
            "size": 0
        }
    ]
}
```

### 3.5 向下兼容

如果请求不带 `client_id` 参数，行为与当前一致（返回所有变更，不过滤）。

---

## 4. 验证方法

改好后，用以下方法验证：

### 测试 1：上传后不回退
1. 两台机器 A、B 都启动
2. 在机器 B 创建文件 `test.txt`，写入内容 "hello"
3. B 上传成功后，等待 10 秒（2 轮 modlist 轮询）
4. **预期**：B 的 `test.txt` 保持 "hello"，不被脱水、不回退
5. **预期**：A 收到 modlist 变更，创建 `test.txt` 占位符

### 测试 2：双向编辑
1. 机器 A 编辑 `test.txt` 改为 "world"
2. A 上传成功
3. 等待 B 的 modlist 轮询
4. **预期**：B 的 `test.txt` 被脱水（cloud mtime 比 B 本地新），双击打开看到 "world"

### 测试 3：重命名同步
1. 机器 B 将 `test.txt` 重命名为 `renamed.txt`
2. 等待 A 的 modlist 轮询
3. **预期**：A 本地 `test.txt` 被删除，出现新占位符 `renamed.txt`

---

## 5. 变更摘要

```
需要改的接口：
  ✏️ PUT  /upload-stream  → 新增 query 参数 client_id
  ✏️ POST /delete         → 请求体新增 client_id 字段
  ✏️ POST /rename         → 请求体新增 client_id 字段
  ✏️ GET  /modlist         → 新增 query 参数 client_id，排除自己的变更，返回磁盘实时值

不需要改的接口：
  ✅ GET  /tree
  ✅ GET  /download
  ✅ POST /register-client
  ✅ GET  /clients
  ✅ POST /remove-client
  ✅ POST /mkdir
```
