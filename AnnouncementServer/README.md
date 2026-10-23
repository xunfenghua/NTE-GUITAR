# NTE-GUITAR 公告服务（Linux x64）

这是一个独立的只读公告服务。更新公告时编辑服务程序目录里的 `announcement.json`，不需要重新编译。

## 发布公告

```json
{
  "enabled": true,
  "id": "nte-guitar-3.1-20261018",
  "version": "3.1",
  "title": "NTE-GUITAR 3.1 已发布",
  "content": "修复问题并改进公告提示。\n下载链接可在下方复制。",
  "downloadUrl": "https://example.com/NTE-GUITAR.zip",
  "publishedAt": "2026-10-18T00:00:00+08:00"
}
```

- `enabled` 设为 `false` 可撤下公告。
- 每次发布请更新 `id`；客户端也会比较版本、标题、正文和下载链接的指纹。
- `downloadUrl` 可留空；如果填写，只接受绝对 HTTP/HTTPS 地址。
- 建议先写到临时文件，再重命名覆盖 `announcement.json`，避免客户端恰好读取到尚未写完的 JSON。

## Linux x64 部署

解压 `NTE-GUITAR-RecommendedScoresServer-linux-x64.tar.gz`，进入目录后启动：

```bash
chmod +x NTE-GUITAR-AnnouncementServer
./NTE-GUITAR-AnnouncementServer
```

程序是 Linux x64 自包含发布，不需要单独安装 .NET Runtime。默认监听 `http://0.0.0.0:2888`；可通过 `ASPNETCORE_URLS` 环境变量更改。

桌面客户端内置了公告服务器地址，无需修改客户端配置。请在云厂商安全组和服务器防火墙开放 TCP 2888；例如使用 UFW：

```bash
sudo ufw allow 2888/tcp
```

可将程序注册为 systemd 服务。假设文件解压在 `/opt/nte-guitar-announcement`：

```ini
[Unit]
Description=NTE-GUITAR Announcement Service
After=network-online.target

[Service]
WorkingDirectory=/opt/nte-guitar-announcement
ExecStart=/opt/nte-guitar-announcement/NTE-GUITAR-AnnouncementServer
Environment=ASPNETCORE_URLS=http://0.0.0.0:2888
Restart=on-failure
User=www-data

[Install]
WantedBy=multi-user.target
```

将内容保存到 `/etc/systemd/system/nte-guitar-announcement.service`，然后执行 `sudo systemctl daemon-reload && sudo systemctl enable --now nte-guitar-announcement`。

如果下载 ZIP 包而不是 tar.gz，解压后先运行 `chmod +x NTE-GUITAR-AnnouncementServer`。

服务按来源 IP 限制读取频率，每分钟最多处理 20 次请求。缺失公告文件或公告关闭时，客户端会收到空公告；JSON 无效或字段不合法时，服务会返回错误状态。

## 推荐谱目录与管理

本服务同时提供推荐谱 API，继续使用默认 `2888` 端口；原公告 API 保持兼容。

- `GET /api/recommendations` 返回 `{ "items": [...] }`，每项包含 `id`、`title`、`artist`、`instrument`、`difficultyLevel`、`difficultyLabel`、`sha256`、`sizeBytes`、`updatedAt`、`midiUrl`。
- 可传 `?query=关键词`（或 `?q=关键词`）搜索曲名/艺术家，传 `?level=1` 到 `?level=16` 按等级筛选。结果按等级升序、曲名排序。
- `GET /api/recommendations/{id}/midi` 提供 MIDI 文件，响应包含 SHA-256 ETag，支持条件请求和 Range 下载。
- `POST /api/recommendations/submissions` 接收公开投稿的 `multipart/form-data`，字段为 `title`、`uploaderName`、`instrument`（`keyboard`/`piano`/`guitar`）、`difficultyLevel`（1–16）、可选 `difficultyLabel` 和 `midi` 文件。`keyboard` 是 36 键脚本谱，`piano` 是游戏内钢琴 MIDI 谱，`guitar` 是音游或吉他脚本谱。成功返回 `202 Accepted` 和待审核 ID；默认每个来源 IP 每分钟最多提交 30 次，采用滑动窗口计数。只有字段、MIDI 文件和重复内容检查通过后才占用次数，400 校验失败和重复文件的 409 不扣投稿额度。可通过 `NTE_GUITAR_SUBMISSION_LIMIT_PER_MINUTE` 调整，范围 1–500。MIDI 文件限 20 MiB，检查 `.mid`/`.midi` 扩展名和 `MThd` 文件头。相同 SHA-256 的文件若已发布或正在审核会被拒绝。
- 打开 `http://服务器地址:2888/admin/recommendations` 管理曲目。输入管理令牌后可上传/更新 MIDI、填写曲名、艺术家、乐器、1–16 难度等级和可选标签，也可删除已发布曲目。更新现有曲目时填写相同 ID 并重新上传 MIDI；留空会自动生成 ID。文件只接受 `.mid`/`.midi`，上限 20 MiB，并验证 MIDI 头。
- 管理页的“待审核投稿”区域显示投稿者、乐器和难度，可试听 MIDI、通过并发布或拒绝。未审核投稿不会出现在公开目录；通过后目录条目包含 `uploaderName`，投稿者作为来源显示，管理员自行发布的曲目该字段为空。拒绝会同时删除投稿文件和元数据。

首次启动前必须设置强随机令牌 `NTE_GUITAR_ADMIN_TOKEN`。令牌仅由管理员在管理页面输入，通过 `X-Admin-Token` 请求头提交，不要嵌入桌面客户端或公开分享。未配置令牌时，管理和审核请求均被拒绝。目录和已审核 MIDI 下载接口是公开只读接口；待审 MIDI 仅管理员可访问。投稿使用独立的来源 IP 限流（默认每分钟 30 次，可用 `NTE_GUITAR_SUBMISSION_LIMIT_PER_MINUTE` 调整），推荐谱目录、MIDI 下载和管理接口也各自限流。

服务在可信反向代理后运行时，可在 `NTE_GUITAR_TRUSTED_PROXY_IPS` 中配置代理 IP（多个地址用逗号分隔），服务会读取该代理转发的客户端 IP，并据此限流。未加入该列表的代理不会被信任，避免客户端伪造转发头绕过限制。例如本机 Nginx 可设置 `NTE_GUITAR_TRUSTED_PROXY_IPS=127.0.0.1`。反向代理必须覆盖客户端传入的 `X-Forwarded-For`，不要原样透传未经验证的值。

推荐谱数据默认保存在程序目录下的 `data/`，包括已发布目录、MIDI 文件和待审队列。生产环境应设置 `NTE_GUITAR_DATA_DIR` 指向持久化目录，并确保服务账户对此目录有读写权限；升级时保留该目录。例如 systemd 配置可增加：

```ini
Environment=NTE_GUITAR_ADMIN_TOKEN=替换为足够长的随机值
Environment=NTE_GUITAR_DATA_DIR=/var/lib/nte-guitar-recommendations
Environment=NTE_GUITAR_SUBMISSION_LIMIT_PER_MINUTE=30
# 仅当服务前面运行本机反向代理时设置；远端代理填其实际来源 IP。
Environment=NTE_GUITAR_TRUSTED_PROXY_IPS=127.0.0.1
```

首次初始化目录可使用 `sudo install -d -o www-data -g www-data /var/lib/nte-guitar-recommendations`。建议通过 HTTPS 反向代理暴露管理页和 API；不要在公开 HTTP 网络中输入管理令牌。运行此升级包不会改变 2888 默认端口，也无需修改客户端端口配置。

在项目根目录执行 `./package_announcement_server_linux.ps1` 会生成独立的 `dist/NTE-GUITAR-RecommendedScoresServer-linux-x64.tar.gz` 和 `.zip`。该脚本只重建自己的推荐谱服务包，不会删除或覆盖已存在的公告服务器包。解压后使用原可执行文件名 `NTE-GUITAR-AnnouncementServer` 启动，systemd 部署方式与上文一致。


## 客户端版本在线数

客户端启动后立即向 POST /api/client-presence/heartbeat 上报程序集版本和本次进程随机生成的会话 ID，之后每 2 分钟续报。会话 ID 只在客户端进程内存中保存，服务端不存设备名、用户信息或持久化安装标识。相同 ID 的心跳会更新原记录；超过 6 分钟无心跳的实例会从在线统计中清除。计数代表活跃客户端进程数，同一设备同时运行多个进程会分别计数。

服务端通过 GET /api/admin/client-presence 返回按版本聚合的活跃实例数；该接口要求 X-Admin-Token，与其他管理 API 一样受管理员限流保护。公开心跳接口限制每来源 IP 每分钟 30 次，并限制请求体大小。活跃状态保存在内存中，服务重启后会清空；在线客户端会在下一次心跳时重新计入。管理页面每 30 秒自动刷新该统计。客户端网络失败时跳过本轮上报，不阻塞 UI 或演奏，并在后续周期继续尝试。
