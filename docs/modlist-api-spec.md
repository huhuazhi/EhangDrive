# modlist 接口规范

## 背景

多台客户端通过亿航Drive连接同一个服务器时，机器A修改了文件并上传，机器B本地已有该文件的水合副本（绿勾），需要得知该文件已被修改。

客户端每5秒轮询 `modlist` 接口获取被修改过的文件列表，与本地比较后决定脱水或上传。

---

## 一、服务端需要做的事

### 1. 维护一个内存字典（modlist）

```
modlist: dict[string, ModRecord]
```

key = 文件相对路径（与 `/tree` 接口返回的 `path` 格式一致）

```json
{
  "path":   "文档/报告.docx",
  "mtime":  1709472000,
  "size":   102400,
  "action": "update"
}
```

### 2. 写入时机

在以下**已有接口**的处理逻辑中，成功执行后追加/更新 modlist：

| 现有接口 | 触发动作 | modlist 记录 |
|---------|---------|-------------|
| `PUT /upload-stream?path=xxx&mtime=xxx` | 文件上传成功后 | `{path, mtime, size, action:"update"}` |
| `POST /delete` body:`{path}` | 删除成功后 | `{path, mtime:当前时间戳, size:0, action:"delete"}` |
| `POST /rename` body:`{old_path, new_path}` | 重命名成功后 | 先记 `{old_path, action:"delete"}`，再记 `{new_path, mtime, size, action:"update"}` |
| `POST /mkdir` body:`{path}` | **不需要记录** | — |

> **同路径去重**：同一个 path 只保留最新的一条记录，直接覆盖旧的。

### 3. 新增一个 GET 接口

---

## 二、接口定义

### `GET /api/sync-config/modlist`

**请求头：**
```
Authorization: Bearer {token}
```

**Query 参数：** 无

**响应：** `200 OK`
```json
{
  "items": [
    {
      "path": "文档/报告.docx",
      "mtime": 1709472000,
      "size": 102400,
      "action": "update"
    },
    {
      "path": "旧文件.txt",
      "mtime": 1709471500,
      "size": 0,
      "action": "delete"
    }
  ]
}
```

**字段说明：**

| 字段 | 类型 | 说明 |
|------|------|------|
| `path` | string | 文件相对路径，与 `/tree` 接口的 `path` 格式一致，使用 `/` 分隔 |
| `mtime` | long | Unix 时间戳（秒），文件最后修改时间（UTC） |
| `size` | long | 文件大小（字节）。`action=delete` 时为 0 |
| `action` | string | `"update"` 或 `"delete"` |

**空列表时：**
```json
{
  "items": []
}
```

---

## 三、伪代码参考

```python
# 服务端内存字典
modlist: dict[str, dict] = {}

# ─── 在 upload-stream 接口成功后调用 ───
def on_file_uploaded(path: str, mtime: int, size: int):
    modlist[path] = {
        "path": path,
        "mtime": mtime,
        "size": size,
        "action": "update"
    }

# ─── 在 delete 接口成功后调用 ───
def on_file_deleted(path: str):
    import time
    modlist[path] = {
        "path": path,
        "mtime": int(time.time()),
        "size": 0,
        "action": "delete"
    }

# ─── 在 rename 接口成功后调用 ───
def on_file_renamed(old_path: str, new_path: str, mtime: int, size: int):
    import time
    modlist[old_path] = {
        "path": old_path,
        "mtime": int(time.time()),
        "size": 0,
        "action": "delete"
    }
    modlist[new_path] = {
        "path": new_path,
        "mtime": mtime,
        "size": size,
        "action": "update"
    }

# ─── GET /api/sync-config/modlist ───
def get_modlist():
    return {"items": list(modlist.values())}
```

---

## 四、注意事项

1. **只用内存字典即可**，服务端重启后 modlist 清空。客户端重启时会自行扫描本地 hydrated 文件与云端对比，不依赖 modlist 的持久性。

2. **无需区分用户/客户端**：modlist 是全局的。客户端收到自己上传的文件记录时，会比对 mtime 发现相同，自动跳过。

3. **path 格式统一**：使用 `/` 作为分隔符，不带前导 `/`。例如 `文档/报告.docx`，不是 `/文档/报告.docx`。

4. **先不管列表增长**：当前阶段 modlist 只增不减，后续可以加清理机制。

5. **并发安全**：modlist 的读写需要线程安全（Python 的 dict 赋值是原子的，Go 需要 sync.RWMutex，Java 用 ConcurrentHashMap）。

---

## 五、客户端调用示例

客户端将这样调用（仅供参考，不需要服务端关心）：

```
GET http://192.168.16.15:8000/api/sync-config/modlist
Authorization: Bearer hhz
```

响应：
```json
{
  "items": [
    {"path": "工作/方案.docx", "mtime": 1709472000, "size": 51200, "action": "update"},
    {"path": "临时/废弃.txt", "mtime": 1709471800, "size": 0, "action": "delete"}
  ]
}
```
