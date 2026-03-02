# 客户端管理 API 服务端改动文档

> 基础路径：`/api/sync-config`  
> 认证方式：`Authorization: Bearer {token}`

---

## 1. 数据库表设计

需要一张 `sync_clients` 表（或等效存储），记录所有注册过的客户端：

| 字段 | 类型 | 说明 |
|------|------|------|
| `client_id` | VARCHAR(36) PK | 客户端 UUID，由客户端生成 |
| `hostname` | VARCHAR(255) | 主机名 |
| `ip` | VARCHAR(45) | 客户端局域网 IP |
| `registered_at` | DATETIME | 首次注册时间 |
| `is_active` | BOOLEAN | `true`=正常，`false`=已被删除 |

> **关键设计**：删除客户端时不要物理删除记录，而是将 `is_active` 设为 `false`。  
> 这样当该客户端重新注册时，能检测到它曾被删除，从而返回 `need_full_sync: true`。

---

## 2. POST `/register-client` — 注册客户端

### 请求

```json
{
    "client_id": "550e8400-e29b-41d4-a716-446655440000",
    "hostname": "DESKTOP-ABC123",
    "ip": "192.168.16.20"
}
```

### 业务逻辑

```
收到 client_id:
├─ 数据库中不存在该 client_id
│   → 新建记录 (is_active=true)
│   → need_full_sync = true （新客户端，需全量同步）
│
├─ 存在且 is_active = true
│   → 更新 hostname、ip（可能换了网络）
│   → need_full_sync = false （正常客户端）
│
└─ 存在且 is_active = false （曾被删除）
    → 设置 is_active = true，更新 hostname、ip
    → need_full_sync = true （被删除过，需重新全量同步）
```

### 响应

```json
{
    "message": "Client registered",
    "client_count": 3,
    "need_full_sync": true
}
```

| 字段 | 类型 | 说明 |
|------|------|------|
| `message` | string | 描述信息 |
| `client_count` | int | 当前 **活跃** 客户端总数（`is_active=true` 的数量） |
| `need_full_sync` | bool | `true` = 该客户端需要执行全量同步 |

> **客户端行为**：收到 `need_full_sync: true` 后，客户端会清除本地同步完成标记，重新执行全量同步（递归拉取所有占位符）。

---

## 3. GET `/clients` — 获取客户端列表

### 请求

无参数。

### 响应

只返回 **活跃** 客户端（`is_active=true`）：

```json
{
    "clients": [
        {
            "client_id": "550e8400-e29b-41d4-a716-446655440000",
            "hostname": "DESKTOP-ABC123",
            "ip": "192.168.16.20",
            "registered_at": "2026-03-03T10:30:00"
        },
        {
            "client_id": "6ba7b810-9dad-11d1-80b4-00c04fd430c8",
            "hostname": "LAPTOP-XYZ",
            "ip": "192.168.16.25",
            "registered_at": "2026-03-03T11:00:00"
        }
    ]
}
```

---

## 4. POST `/remove-client` — 删除（注销）客户端

### 请求

```json
{
    "client_id": "550e8400-e29b-41d4-a716-446655440000"
}
```

### 业务逻辑

```
找到该 client_id:
├─ 存在 → 设置 is_active = false （软删除）
│         → 返回 200
└─ 不存在 → 返回 404
```

> **不要物理删除！** 保留记录才能在客户端重新注册时检测到"曾被删除"，触发 `need_full_sync`。

### 响应

```json
{
    "message": "Client removed"
}
```

---

## 5. 完整流程示意

```
客户端 A 和 B 都已注册 (is_active=true, client_count=2)
    │
    ▼
用户在客户端 B 的设置页 → 删除客户端 A
    │
    ▼
B 调用 POST /remove-client { client_id: "A的ID" }
    │
    ▼
服务端：A 的 is_active = false
    │
    ▼
客户端 A 下次启动 → 调用 POST /register-client
    │
    ▼
服务端发现 A 的 is_active = false
    → 恢复 is_active = true
    → 返回 { need_full_sync: true, client_count: 2 }
    │
    ▼
客户端 A 收到 need_full_sync = true
    → 清除本地同步标记
    → 执行全量同步（重新拉取所有文件占位符）
    → 确保与服务端完全一致
```

---

## 6. 注意事项

1. **`client_count` 只计算 `is_active=true` 的客户端**。客户端用这个值判断是否启动 modlist 轮询（≥2 才启动）。

2. **首次注册的新客户端也应返回 `need_full_sync: true`**，因为新客户端本地没有任何文件，也需要全量同步。（客户端自身也有 marker 文件机制兜底，即使服务端不返回 true，新客户端也会执行全量同步。但服务端返回 true 可以覆盖"marker 文件意外存在"的边缘情况。）

3. **`registered_at` 格式**：建议使用 ISO 8601 格式（如 `2026-03-03T10:30:00`），客户端目前只做展示，不解析。
