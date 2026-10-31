# HoloLensCaptureDemo

HoloLens 1 单次拍照、电脑接收并返回序列号、世界空间标签的 Unity Demo，支持电脑端工作归档、恢复标签和按类别清理缓存。

工程目录：`C:\Users\Thinkpad\Documents\HololensWorkspace\HoloLensCaptureDemo`。Windows 上的 Unity 构建及部署工具链在同级 `HoloLens1Environment`，Unity 固定为 **2019.4.41f2**；使用这些构建脚本时需保留两个目录的相对位置。独立运行接收服务和 Web 不需要 Unity 或该工具链。

## Windows / Linux / macOS 共用服务端

三个平台运行同一份 `server/server.py` 和 `server/web/`，只需 **Python 3.11+（包含 sqlite3）**，当前 Demo 无第三方 Python 包、Node.js 或数据库服务依赖。路径使用 Python 的跨平台 [`pathlib`](https://docs.python.org/3/library/pathlib.html)，照片和轨迹保存在本地 SQLite 与 JPEG 文件中。精简 Python 发行版需确认包含 [`sqlite3`](https://docs.python.org/3/library/sqlite3.html)。

| 部分 | 运行位置 |
|---|---|
| 拍照、头部追踪、空间锚点、显示返回结果 | HoloLens 1 |
| 接收照片、图像处理入口、响应、离线补传、姿态与工作归档 | Windows / Linux / macOS，同一套 Python 代码 |
| 照片、结果、轨迹、工作恢复与缓存管理的 Web 页面 | 三个平台的浏览器，同一套 HTML / CSS / JavaScript |
| Unity → UWP x86 编译、签名和部署 | Windows |

独立启动入口在前台运行，服务启动后**默认自动打开 Web 查看页**，按 **Ctrl+C** 停止。已有配置时，在工程目录运行：

```powershell
# Windows PowerShell
.\RunServer.cmd
```

```sh
# Linux / macOS
sh ./RunServer.sh
```

无需再传 `--open-browser`；在没有桌面的服务器上，可加 `--no-browser` 只启动服务。`--init-config` 仅生成配置，不启动服务或打开浏览器。

两者均调用同一个 Python 服务；所有平台也可直接执行 `python server/server.py --config config/server.local.json`（Linux / macOS 通常使用 `python3`，直接运行 Python 时仍需 `--open-browser` 才会打开网页）。可用 `--config <路径>` 指定其他配置，`--help` 查看参数；默认配置路径固定相对于工程，不受启动时工作目录影响。图片存储路径相对于**配置文件所在目录**解析，建议配置使用 `../server/data` 这类正斜杠相对路径，便于迁移。

启动脚本优先使用 `HOLOLENS_PYTHON` 指定的 Python 可执行文件，其次使用工程 `.venv`。Windows 随后尝试已有共享 Python、`py -3` 和 `python`；Linux / macOS 随后使用 `python3`。因而可以直接复用本机工具链，也可以只安装普通 Python。现有 `StartServer.cmd` / `OpenMonitor.cmd` / `StopServer.cmd` 保留为本机 Windows 后台启动入口，仍使用已准备的共享环境；独立入口不受这些脚本限制。

**首次配置：**如果还没有配对配置，先执行下面对应平台的一条命令，再正常启动：

```text
Windows:       .\RunServer.cmd --init-config
Linux / macOS: sh ./RunServer.sh --init-config
```

生成器仅创建缺失的 `config/server.local.json`，生成随机密钥且不在终端打印；配置已存在时拒绝覆盖。`config/server.example.json` 说明字段，示例密钥不能直接用于启动。`host` 默认 `0.0.0.0` 接收局域网连接，`port` 默认 `8765`，`delaySeconds` 默认 `1`。新生成的密钥必须同步给 Windows 构建机上的客户端配置，再构建头显应用。

**把当前已配对设备的接收端迁移到 Linux / Mac：**

1. 在目标电脑放置工程代码和 Python；仅运行服务时，保留 `server/`（含 `web/`）、`config/` 和对应 `RunServer` 启动脚本即可。
2. 将当前的 `config/server.local.json` 私下复制到目标电脑的同一相对位置，保留与头显一致的 `apiToken`；不要重新生成密钥。需要迁移历史照片、轨迹与工作归档时，先停止原接收服务，再完整复制 `server/data/`，包含 `work.sqlite3`。电脑只保存和传输空间锚点字节，不需要 Windows 空间定位运行库；实际定位由头显完成。
3. 在目标电脑启动 `RunServer`，允许头显所在局域网访问目标电脑的 TCP 8765。浏览器在**该接收电脑**打开 `http://127.0.0.1:8765/`。浏览器页面无需填写配对密钥。
4. 当前头显包中的服务器地址在构建时写入。若目标电脑地址不同，在 Windows 构建机执行下列命令（把示例 IP 换成目标电脑的地址），随后按已有流程构建、签名、升级安装：

   ```powershell
   powershell -ExecutionPolicy Bypass -File scripts\Configure-Lan.ps1 -Address 192.168.100.50
   .\BuildDemo.cmd
   powershell -ExecutionPolicy Bypass -File scripts\Sign-Demo.ps1
   .\DeployUsb.cmd -DeviceId <头显设备ID>
   ```

   `Configure-Lan.ps1` 保留 Windows 构建机现有配置中的密钥；若目标接收端另行生成了新密钥，先将其 `server.local.json` 同步回构建机再执行。目标平台的图像处理代码更新不需要重编译头显；修改服务器地址、配对密钥或头显接口/显示逻辑时才需要更新头显包。

查看页目前仍限接收电脑本机访问；跨平台运行不等于开放远程网页。无桌面的 Linux 可从自己的电脑使用 SSH 本地转发 `ssh -L 8765:127.0.0.1:8765 user@接收机地址`，再打开本机 `http://127.0.0.1:8765/`（该本地端口需空闲）。头显继续直接访问接收机局域网地址。

当前图像处理仍是保存 JPEG 后 `sleep(delaySeconds)` 并返回序列号，尚未接入识别模型。后续推理写在相同 Python 处理入口，三平台共用代码；模型运行库及 GPU 加速后端需按所选框架适配平台。当前客户端对序列号和中心坐标有约束，新增识别结果字段需同步调整头显显示，详见下文接口说明。

2026-10-31：**29 项测试已在 Windows 原生 Python 和 WSL/Linux 上分别通过**，覆盖不带 Unity/共享工具链的独立副本、含空格路径、从其他工作目录启动、Windows BOM 配置、密钥不输出及不覆盖、照片与姿态上传、网页资源与原图读取。另修正了提前拒绝上传时未接收完请求体导致 Windows 偶发连接重置的问题，受限时长的请求体清理已通过 20 次重复回归。日志位于 `logs/portable-server-tests-{windows,linux}.log` 和 `logs/portable-http-rejection-windows.log`。macOS 使用同一代码与 POSIX 启动脚本，**尚未在 Mac 实机执行验证**；可运行 `python3 -m unittest discover -s tests -v` 完成同一套检查。

## 先在电脑上运行

1. 双击 `StartServer.cmd`，启动 Windows 本机图片接收服务。
2. 双击 `OpenDemo.cmd`，等待 Unity 打开场景，点击 Play。
3. 选中 Game 视图，按 **Space** 触发一次上传；约 1 秒后看到 `HL1-000001` 一类标签。
4. 按住鼠标右键转动视角，用 **W/A/S/D** 移动。可以在等待返回时就移动，标签仍留在拍照时的位置。
5. 再按 Space 产生另一个标签；**Backspace** 清空标签。`StopServer.cmd` 停止接收服务。

编辑器用程序生成的 JPEG 测试图，网络、图片保存、延迟和序列号均走真实服务。模拟模式保存在内部状态中；1.0.11.0 起不再显示底部状态文字。

当前接收地址为 `http://192.168.100.92:8765`（2026-10-31，Wi-Fi `e1-102`，实体头显为 `192.168.100.61`）。此前地址为 `192.168.0.113`。电脑换网络或 IP 后，关闭 Unity，运行 `Configure-Lan.cmd`，再打开或重新构建应用。该脚本保留已有配对密钥，并可配置仅允许本地子网访问、限定 Python 程序和端口的 Windows 防火墙规则。

## 在浏览器中查看图片与响应

双击 **`OpenMonitor.cmd`**，或在这台电脑的浏览器打开 **<http://127.0.0.1:8765/>**。快捷入口会在需要时自动启动接收服务，无需另外安装网页工具或启动 Unity。

- 每秒自动刷新上传历史，默认跟随最新图片；点击历史记录后保持当前选择，可点击“跟随最新”恢复。
- 查看上传的 JPEG 原图、返回的序列号、图像坐标、处理耗时、来源 IP 和请求 ID；可开关标签叠加预览、下载原图。
- 搜索序列号或请求 ID，查看每次重试的发送状态和完整响应 JSON，支持复制。点击有响应的请求行可查看该次返回值。
- 右上角“清除图片记录”会先弹出确认框，再删除全部照片记录、对应原图和响应（包括搜索范围之外的记录）。有上传正在进行时会提示稍后重试；清除后序列号继续递增，头显中已显示的标签和已经完成的工作归档不受影响。其他查看页在下次刷新时同步移除旧记录。仅释放磁盘空间时，优先使用“清理缓存”并按类别选择。
- 自动刷新在后台进行，不会反复禁用“立即刷新”按钮。关闭自动刷新后，仍可手动刷新。
- 页面区分处理中、已发送响应、发送断连和服务中断。照片列表中的**“已发送响应”仅表示服务端成功向连接写出数据，并不是头显显示成功的回执**。工作恢复和清理指令另有设备执行回执，显示在“工作历史与恢复”区域。

网页中的标签是按返回坐标叠加在照片上的预览，不能用它判断真机世界锚点是否稳定。页面启用前的图片和序列号仍可查看，但会注明缺少当时的响应明细。相同请求 ID 的重试合并在同一图片记录中。

查看页及图片查询接口仅限接收电脑本机访问，无需在浏览器输入配对密钥。头显仍通过原有局域网上传接口发送图片。关闭网页不影响接收，关闭服务后数据仍保存在 `server/data/`；没有自动删除历史图片的机制。

## 系统直播（调试版）

网页右上角点击 **“系统直播”**，或访问 `http://127.0.0.1:8765/system-live`。Windows 可双击 **`OpenSystemLive.cmd`**；也可运行 `OpenSystemLive.cmd -DeviceIp 192.168.100.61` 预填设备地址。Linux/macOS 继续通过同一份服务和 Web 页面操作，不需要媒体转码库或重新编译头显应用。

1. 头显开机、保持唤醒，与电脑连接同一 Wi-Fi，并开启开发者选项中的 Device Portal。
2. 输入头显 IPv4，点击“设备门户登录”。首次使用在官方页面点击 Request pin，将头显 PIN 填回网页，自行设置门户账号密码；后续复用同一浏览器中的登录。该账号不同于 Demo 上传配对密钥，工程不会读取或保存它。
3. 选择低/中/高画质，以及是否包含全息标签，点击“开始直播”。视频直接显示在当前控制页右侧，不再弹出播放页；可用“全屏”放大查看。默认中等画质、包含全息内容，麦克风及应用音频均关闭。只有主动点击“设备门户登录”时才打开官方登录页，用于首次登录或处理证书提示。
4. 修改选项后点击“重新开始直播”；旧视频连接会先断开。点击“停止直播”释放本页占用的摄像头；离开或刷新控制页也会停止。首次切换到内嵌版时，请关闭以前的播放窗口及官方 Live preview。

**1.0.10.0 起支持在直播期间直接合拢拍照。**保持系统直播控制页打开，在前台 `HoloLensCaptureDemo` 中合拢一次，流程为：内嵌播放器显示拍照提示并断开视频连接 → 头显拍一张并保存 → 本页播放器自动恢复直播。无需逐张点击“停止直播”或再次确认；保持合拢不会连拍，拍照期间的新手势也不会排队补拍，松开后可再触发。首次使用新版控制页前，请关闭此前手动打开的直播及 Device Portal 的 Live preview；自动切换仅控制本页创建的视频连接。基础直播可搭配旧 App 使用，自动切换需要新版 App 和新版接收服务。

[一代设备的系统直播与 App 拍照争用摄像头](https://learn.microsoft.com/en-us/windows/mixed-reality/develop/advanced-concepts/using-the-windows-device-portal#mixed-reality-capture)。电脑按头显请求的来源 IPv4 协调对应直播，等待所有受控播放器卸载视频源后确认暂停，再给相机 750 ms 释放时间。仅调用视频暂停不会释放摄像头，因此这里会移除源并重新加载媒体元素。暂停协调超过约 6 秒未确认时取消本次拍照；正常成功及拍照失败后都会请求恢复。拍照端意外退出或释放消息未到达时，临时暂停最长约 120 秒后过期；网页协调连接失败则断开它管理的视频连接，需要重新开始。等待期间用户点击停止或离开网页，不会在拍照结束后又恢复播放。

直播控制页使用最长 2 秒的本机长轮询，拍照请求到达时立即唤醒；这段流量在浏览器与电脑之间。头显只在拍照时请求暂停/恢复，等待确认时约每 150 ms 重试。无法联系电脑时仍会尝试离线拍照和暂存，但必须有可用摄像头；未受控直播占用相机时可能失败，原因记录在应用日志中。视频由浏览器直接连接头显，属于独立的持续网络流量。本版本不录制、不归档视频、不做实时推理，也不从视频中恢复空间位置。

使用官方 `/api/holographic/stream/live_{low,med,high}.mp4`，显式设置 `pv=true`、`holo=true/false`、`mic=false`、`loopback=false`。标称档位分别是 428×240 / 15 fps / 0.6 Mbps、854×480 / 30 fps / 2.5 Mbps、1280×720 / 30 fps / 5 Mbps，来自 [官方直播接口](https://learn.microsoft.com/en-us/windows/mixed-reality/develop/advanced-concepts/device-portal-api-reference#mixed-reality-streaming)，不是实测结果。播放器嵌入本机控制页，浏览器直接读取设备的 HTTPS 视频流，使用该浏览器中的门户登录。视频不经过 Python 代理，工程不读取或保存门户密码，不把账号密码加入 URL。每次连接使用新的 `streamSession` 标识，避免媒体缓存重播旧连接的片段。

控制页先显示“正在连接”，收到浏览器实际播放事件后才显示“直播中”及视频分辨率；拍照时显示“正在拍照”。等待画面超过 15 秒或播放出错时断开连接并提示检查，不会自动弹出播放页。若无法播放，先通过“设备门户登录”确认登录与证书，然后回到本页重新开始；同时检查休眠、IP、其他直播和拍照占用。控制页提供本机参考时钟，可让头显拍到它，对比直播中的时间人工估算端到端延迟。画质菜单的帧率不能当作实际延迟；一代 MRC 合成视角也不能直接代替头显内的锚点精度验收。

2026-10-31：实体 HoloLens `192.168.100.61` 已完成 Device Portal 首次配对，用户在笔记本确认**中等画质系统直播可见，转头后画面持续更新**。尚未测量实际帧率、延迟、长期稳定性或各画质的性能差异。页面配置、开始/停止、重新打开、弹窗拦截提示与窄屏布局的检查记录见 `logs/system-live-browser-smoke.json`。

2026-11-01 内嵌播放更新：Edge 已用合成动态视频验证真实视频解码、加载/播放状态、视频源卸载后再确认拍照、无需新手势的自动恢复、拍照期间重启/停止、媒体错误与协调错误清理、页面退出清理及窄屏布局。记录为 `logs/inline-live-browser-smoke.json`，截图为 `logs/inline-live-{desktop,mobile,capturing}.png`。Windows/Linux 的 46 项服务端测试通过；正式接收服务升级保留并校验原有 23 张照片，见 `logs/inline-live-server-update.json`。随后用户在真实 HoloLens 上确认：视频正常嵌入当前页面，合拢拍照后画面自动恢复；无需弹出直播窗口。已安装的 App 1.0.11.0 可直接使用本次网页更新。记录见 `logs/inline-live-device-verification.json`。

## 工作历史、恢复与清理缓存

新版 App 首次启动会生成设备身份和当前工作 ID。完成照片上传后，再将序列号、工作归属和可导出的空间锚点归档到电脑的 `server/data/work.sqlite3`。照片收到有效响应后从头显上传队列移除；空间备份收到电脑持久化确认后才清理头显的待归档文件和对应本地锚点存储。电脑端工作归档没有自动过期时间，关闭网页、重启服务不会删除它。

网页顶部“工作历史与恢复”的使用步骤：

1. 打开新版头显 App，保持前台运行并与电脑连通，在“目标设备”中选中头显。
2. App 默认继续当前工作；也可以填写名称并点击“开始新工作”。旧工作的归档会保留，尚未上传的照片仍属于原工作。
3. 选择需要继续的工作，点击“恢复工作到头显”。指令先显示等待执行，收到回执后显示载入、已定位和不可恢复的标签数量。离线设备的恢复指令保存在电脑，重新连通后处理。
4. App 重开时会尝试自动恢复当前工作，默认只恢复最近 **12 个标签**（由 `maxLabels` 决定）。仅下载标签和空间锚点，原始 JPEG 留在电脑；断网时可使用上次已经下载的有效缓存。

真机使用 [WorldAnchorTransferBatch 的本地空间锚点传输](https://learn.microsoft.com/en-us/windows/mixed-reality/out-of-scope/local-anchor-transfers-in-unity)，不是直接把上次启动的 XYZ 坐标用于新会话。恢复时应回到原拍摄区域并环顾周围，让设备重新识别环境；未定位的标签会隐藏，避免在错误位置显示。恢复回执中的“部分完成 / 等待定位”不代表所有标签都已可见，设备状态会继续报告当前已定位数量。编辑器归档明确标记为模拟位置，不能用于真机空间恢复。**旧版照片没有空间锚点备份，无法补推其原空间位置**；未成功保存锚点的记录也只能保留结果。

点击网页右上角 **“清理缓存”**，仅勾选需要清理的类别，再点击“清理所选项”。默认全部不选：

| 位置与类别 | 清理效果 | 恢复能力 |
|---|---|---|
| 电脑：历史照片原图 | 删除已接收的 JPEG，保留照片元数据、结果和工作归档 | 标签仍可恢复；原图无法再次查看或下载 |
| 电脑：历史姿态轨迹 | 删除已接收的轨迹采样及会话，压缩姿态数据库 | 旧轨迹无法恢复；不影响空间锚点，前台 App 会继续上传新点 |
| 头显：已下载的历史锚点 | 删除下载缓存与对应清单 | 联网后可从电脑重新下载；清理后无法离线使用该缓存 |
| 头显：当前显示的标签 | 释放已完成标签的显示对象 | 可从电脑恢复；待备份锚点仍保留，待上传标签不删除 |
| 头显：未上传照片 | 删除所选设备整个待上传照片队列及其未归档空间位置 | 可能尚无电脑副本，删除后可能永久丢失 |
| 头显：未上传姿态 | 删除尚未同步的姿态队列 | 这些轨迹点丢失；之后继续采样，并记录缺口 |

删除原图、历史轨迹或未上传数据时必须额外勾选丢失确认。这里按**类别**清理全部对应缓存，不是逐张照片选择。电脑上的标签与空间锚点工作归档不会被缓存清理删除；这也意味着清理缓存不会释放工作归档数据库占用的空间。身份、服务器配置和未归档空间锚点不提供普通缓存清理选项；主动丢弃未上传照片时会连同这些照片的空间位置一起移除。本功能不清理 HoloLens 系统的环境扫描地图。

头显缓存清理只允许向近期在线的设备发送，指令 **30 秒后过期**；网页以设备回执判断完成，提交成功不等于清理完成。设备采集或上传繁忙时会等待，仍忙则拒绝清理并提示重试。执行标记与回执保存在设备上，断线重传不会反复清理后来产生的数据；执行过程中退出应用会报告中断，需要检查后重新下发。电脑有相关上传进行时会拒绝清理，请稍后重试。

头显历史目录位于 `LocalState/CaptureOutbox-history`（编辑器为 `logs/editor-outbox-history`）。待归档队列上限为 **100 条 / 128 MiB**，单个空间备份最多 **32 MiB**；下载锚点缓存最多 **64 MiB**，超过时先淘汰旧下载。达到待归档上限后暂停新增拍照，保留已有数据，等待备份恢复。不要卸载应用来升级，否则设备身份、当前工作与未上传内容会随私有存储丢失。

## 在 HoloLens 1 上运行

**1.0.11.0 界面调整（已通过 USB 保留数据升级并核实版本）：**删除底部状态文字及其字体对象，常驻信息只保留左上角 Wi-Fi、IPv4 和 Pose 1Hz。拍照状态仍保存在 `CaptureController.StatusMessage`，拍照异常仍写入应用日志。中央准星与拍照生成的空间标签继续用于对准和查看结果。 UWP x86 编译与签名通过，已核实打包场景中没有底部 HUD 对象，记录见 `logs/minimal-hud-package-verification.json`。

2026-10-31 已通过 USB 在实体设备 `HoloLens-7V9C5` 上安装并升级到 **1.0.8.0**，包含拍照、照片离线队列、网络 HUD 与 1 Hz 姿态轨迹。本次使用 `update` 保留应用数据，修正电脑接收地址并增加明确的中文拍照提示。版本核实结果见 `logs/usb-deployment.json`；第一次拍照时需在头显中允许摄像头权限。USB 用于部署，现有图片和姿态上传仍通过 Wi-Fi 进行。编译和编辑器两次实际 HTTP 上传测试已通过，升级后的真机拍照/姿态接收等待佩戴设备验证。

2026-11-01 已通过 USB 保留数据升级并核实安装 **1.0.10.0**，包含工作历史、缓存清理、合拢即拍与直播自动暂停/恢复；电脑接收服务和网页已更新。同次佩戴验证中，用户确认合拢一次拍一张、保持合拢不连拍、标签显示及直播自动暂停/恢复均正常；电脑收到来自 `192.168.100.61` 的 `HL1-000021`、`HL1-000022`、`HL1-000023`，单次服务端处理约 1.05 秒。此耗时不含直播交接、相机启动和网络端到端延迟。安装记录见 `logs/usb-deployment.json`，包核对见 `logs/camera-handoff-package-verification.json`，真机结果见 `logs/camera-handoff-device-verification.json`。

后续 USB 升级可运行 `DeployUsb.cmd -DeviceId 34d02739-c8d0-45bd-119f-54ea5fbd3d97`。脚本检查最新已签名真机包及接收密钥，已安装时使用 `update` 保留应用数据，安装后再次查询核实版本。首次连接新设备时，按 Windows SDK 提示在头显开发者选项点击 Pair，并添加 `-Pin <临时配对码>`；配对成功后通常不需重复输入。脚本不会卸载已有应用。

1. 电脑运行 `StartServer.cmd`，头显连接同一局域网。
2. 关闭本工程的 Unity 窗口，运行 `BuildDemo.cmd`。
3. 用共享环境中的 Visual Studio 2019 打开 `builds\UWP\HoloLensCaptureDemo.sln`，选择 **Release / x86 / Remote Machine**，填写头显 IP，并按 VS 的配对提示部署运行。头显需开启开发人员模式。
4. 允许摄像头权限。看向要标记的位置，做一次 **Air Tap（空中点击）**。
5. 应用拍一张不含全息内容的照片，上传到电脑；收到返回值后显示序列号。转头、走动，检查标签仍留在原位置。

**Demo 的拍照入口：**打开 `HoloLensCaptureDemo` 并保持前台，看到准星和左上角的 Wi-Fi、IPv4、Pose 1Hz 信息后，将手举到头显可见范围，伸出食指，再向下弯曲/合拢（Air Tap 的按下动作），便直接进入拍照、保存和上传流程。无需先选按钮、系统菜单或等待松开；保持合拢只触发一次，松开后才可再拍。两次有效按下最少间隔 0.3 秒，忙碌期间的新按下不会排队；等本次拍照结束再松开、合拢即可。这里使用一代设备的 Select 按下事件，并非 HoloLens 2 的关节级捏合识别。首次摄像头授权仍是系统权限步骤。

**系统相机与 Demo 是两个入口：**在沉浸式 App 内张手（Bloom）出现的五个图标属于 HoloLens 系统快捷菜单；其中“拍照”进入系统混合现实相机，将照片保存至系统相册，不会调用 Demo 的上传接口。这个系统拍照入口及其手势提示不能由 Demo 改写。需要上传照片、返回序列号并生成世界标签时，使用上述 Demo 的一次 Air Tap 操作。参见微软的[系统拍照说明](https://learn.microsoft.com/en-us/hololens/holographic-photos-and-videos)。

编译脚本产出的是用于验证编译的**未签名**包。每次 Unity 导出自动递增 UWP 包版本，避免修改内容后仍使用相同版本而被系统拒绝。`scripts\Sign-Demo.ps1` 签名最近构建的包并生成 APPX，或者使用 VS 的开发部署。签名脚本将不可导出的私钥保存在 Windows 当前用户的证书存储中，输出公共 `.cer`，不会修改宿主机的受信任根证书。

`BuildSimulatedDemo.cmd` 生成 `builds\UWP-Simulated`，供模拟器使用程序生成的照片。这个版本在配置中明确标记模拟模式；真机版本不会在拍照失败时改用测试图。模拟器的安装与启动说明见同级环境的 `docs\EMULATOR.md`。

## 在已安装的模拟器中运行

1. 双击同级环境中的 `HoloLens1Environment\OpenEmulator.cmd`，等待进入系统。
2. 本工程中运行 `StartServer.cmd`，然后运行 **`DeployEmulator.cmd`**。模拟图片版已经构建；修改源码后先关闭 Unity，运行 `BuildSimulatedDemo.cmd` 再部署。
3. 选中模拟器窗口，在人形输入模式下按 **Enter 或鼠标右键** 模拟 Air Tap；**WASD** 移动，鼠标拖动或方向键转动视角。标签在拍照时的中心射线前方 2 米生成，移动后仍留在原位置。
4. 如需按 Space 或 Backspace 直接控制应用，切换右侧的键盘输入模式；Backspace 清空本次运行的标签。

部署脚本通过已记录的模拟器网卡 MAC 查找当前 IP，因此支持本机重启后 IP 改变。自动查找失败时，可在 Windows 终端运行 `DeployEmulator.cmd -Address <模拟器当前IP>`。脚本会核对目标确实是一代 HoloLens 虚拟机，再生成开发签名并使用 **Windows SDK 的 WinAppDeployCmd** 安装，最后通过 Device Portal 启动应用。

本机旧版 Device Portal 的直接包安装接口曾返回 `0x80004005`，SDK 部署方式已验证成功。这里使用 SDK 的 `install`，避免按 Device Portal 跨用户包列表决定 `update` 时出现“上一版本未安装”的错误。重新启动模拟器后，按上述步骤重新部署即可；模拟器内数据不作为持久存储。需要断点调试时再用 VS 打开生成的解决方案。

多次拍照会保留多个标签；在完全相同的位置连续拍照时，标签会重叠，可先清空或移动后再拍。

## 左上角设备网络信息

App 内左上角常驻显示 `Wi-Fi: <当前 SSID>` 和 `IPv4: <该 Wi-Fi 网卡的地址>`。它跟随视线，在拍照、上传、等待结果和报错期间均保留。切换网络后根据系统事件刷新，并每 5 秒重新读取一次；从系统设置返回 App 时也会刷新。断网或尚未获取地址时会明确显示相应状态。

无需访问互联网来显示局域网信息。模拟器通过虚拟网卡连接，显示 `Wi-Fi: -- (Ethernet / virtual)` 及模拟器 IP；Unity 编辑器显示 `NETWORK (EDITOR PC)` 及电脑 IP，不将它冒充头显地址。电脑切换到对应 Wi-Fi 后，可以使用头显显示的 IP 进行部署或访问 Device Portal；此功能不会自动修改电脑的网络或 App 的接收服务器地址。

实现使用 Windows 的 [NetworkInformation](https://learn.microsoft.com/en-us/uwp/api/windows.networking.connectivity.networkinformation) 和 [GetConnectedSsid](https://learn.microsoft.com/en-us/uwp/api/windows.networking.connectivity.wlanconnectionprofiledetails.getconnectedssid)，按网卡 ID 配对 SSID 与 IPv4；网络事件只标记刷新，由 Unity 主线程更新文字。

## 通信方式、离线队列与局域网

头显使用**触发式拍照 + 1 Hz 姿态上报 + 约 2 秒一次的工作指令轮询**；这条应用数据链路不传持续视频，按需开启的系统直播另占持续带宽。每次合拢 / Space 拍摄一张 JPEG，先写入设备队列，再尝试连接电脑；电脑通过同一次 HTTP POST 的响应返回标签。新增工作通道上报缓存数量、当前工作与状态，并接收恢复或清理指令；长时间空间导入/导出期间也发送状态心跳。通常每次轮询结束后等待 2 秒，网络超时会延长间隔。空间备份只在归档或恢复时传输，不会每 2 秒传一份地图。网页的照片刷新与工作状态刷新只发生在浏览器与电脑本机服务之间。下面的状态表仅描述照片队列；没有照片时，前台 App 仍会发送姿态和工作状态。

| 状态 | 头显的网络行为 |
|---|---|
| 无待上传照片 | 照片队列不探测、不上传；姿态及工作通道仍运行 |
| 新的一批照片 | 先检查一次 `GET /health`，成功后串行 `POST /api/captures` |
| 电脑不可达且队列非空 | 每 2 秒启动一次健康检查，单次探测超时 2 秒，不并发探测、不反复发送大图 |
| 网络恢复 | 按入队顺序补传，一张收到并验证响应后才处理下一张 |
| 上传中断或 408 / 429 / 5xx | 保留照片，重新进入 2 秒探测；每次重新完整上传该 JPEG |
| 其他 HTTP 错误或响应异常 | 保留数据并暂停，原因保存在内部上传状态中；修正电脑配置后合拢会唤醒队列并拍新照片；R 只重试队列 |

健康检查只访问 `serverUrl` 配置的电脑，不扫描局域网、不自动发现变更后的 IP。正在执行的图片上传仍使用 `requestTimeoutSeconds`（默认 15 秒），超时后才进入重连检查。因此断线发生在上传过程中时，首次恢复检测可能先经历这段超时。

队列默认最多 **20 张 / 64 MiB 图片数据**，任一限制达到就暂停新增拍照，保留旧照片等待电脑。UWP 队列位于设备应用私有存储 `LocalState\CaptureOutbox`；编辑器队列位于工程 `logs\editor-outbox`。JPEG 与元数据完整写入后才成为正式队列项；收到合法响应后才移出队列。每张照片的 `captureId` 固定，响应丢失后的重试仍沿用它，利用接收服务的幂等机制避免重复序列号。

在应用前台运行时，断线后可以继续拍照，上传工作不占用拍照按钮。退出或系统挂起期间不会继续轮询；重新打开应用会读取本地队列继续处理。重启同一应用会保留队列，卸载应用或清除应用数据则不会。电脑网页的“清除记录”不会删除设备队列，恢复连接后暂存照片仍会上传。

待上传照片会尝试将拍照时的 `WorldAnchor` 保存到设备的 [WorldAnchorStore](https://learn.microsoft.com/en-us/windows/mixed-reality/develop/unity/spatial-anchors-in-unity)。重开应用后先加载锚点，再填写补传返回的标签；暂时未定位时隐藏文字，等待空间追踪恢复。如果锚点保存或恢复失败，照片仍可上传并在网页查看，应用不会把旧标签放到新的视线前方。

局域网需要头显与电脑能够互通、允许电脑 TCP 8765 入站；当前防火墙规则限定本地子网。不需要公网连接。可为电脑设置 DHCP 地址保留，避免换地址后重建客户端配置。图片容量估算使用 **JPEG 字节数 × 每秒拍摄次数 × 8**；例如每张 1 MB、每 5 秒一张，平均约 1.6 Mbps。还需计算空间锚点归档/恢复的突发传输量，并留协议、状态请求和重传余量。空间备份大小取决于环境，最多允许每条 32 MiB，不能用编辑器的几个坐标字节代替评估。当前模拟图只有约 14 KiB，也不能代替真实照片。恢复补传时，每张还要等待服务端默认 1 秒处理，因此实际照片补传速度低于每秒一张。

## 1 Hz 头部姿态与空间轨迹

浏览器打开 `http://127.0.0.1:8765/#trajectory`，或点击“空间轨迹”。App 启动后，无需拍照，每秒读取一次 Unity 主相机的位置和旋转并上报。该频率只是应用采样/传输频率，不改变 HoloLens 内部实时追踪的频率，也不上传 SLAM 特征图或摄像头视频。微软的 [Unity 坐标系说明](https://learn.microsoft.com/en-us/windows/mixed-reality/develop/unity/coordinate-systems-in-unity) 与 [追踪丢失处理](https://learn.microsoft.com/en-us/windows/mixed-reality/develop/unity/tracking-loss-in-unity) 描述了相机位姿、空间锚点和追踪状态接口。

- 每次初始化生成独立 `sessionId`。首次有效定位时建立会话原点；X 向启动时右侧，Y 向上，Z 为启动时水平朝向，位置单位为米。真机将该参考物体设为 `WorldAnchor`，每次取头部相对它的位置和四元数。原点在头部初始高度，网格不是实际地板。
- 每个采样记录序号、会话经过时间、UTC 时间、追踪状态、轨迹段号、XYZ 与 XYZW 四元数。不是 GPS/房间绝对坐标；不同会话不自动对齐。历史点不会随 SLAM 后续优化重新计算，大范围或长时间使用仍可能出现漂移。
- 只把 `Active` 且会话锚点已定位的值作为有效位置。其他状态不显示虚假的零坐标。状态变化、超过 2.5 秒的采样间隔、序号缺口会断开连线。Unity 默认可能在失去追踪时暂停更新，这类情况以时间缺口呈现。系统挂起或 App 退出时不采样、不传输；恢复时不伪造中间位置。
- 照片和姿态使用独立上传流程。姿态正常时约每秒一个小 JSON，请求超时 2 秒；断连后每 2 秒健康检查，恢复后按最多 60 个点一批补传，没有照片推理的 1 秒等待。照片和姿态都断线且有积压时，各自每 2 秒探测一次。
- 姿态先写入设备磁盘，最多保留 **7200 个未确认点**（正常 1 Hz 下约两小时）；到上限后保留旧点、跳过新点，并在左上角 HUD 显示 `skipped` 数量。成功上传的段从队列移除。重复 `(sessionId, sequence)` 不重复插入；响应丢失、文件删除失败或重启后的重复补传都保持同一条数据。旧会话待上传文件在 App 重开后仍会补传，新采样属于新会话。
- 设备队列位于 `LocalState/CaptureOutbox-poses`，编辑器位于 `logs/editor-outbox-poses`。服务端另存 `server/data/poses.sqlite3`，重启接收服务保留历史。网页的“清除图片记录”不删除轨迹。HTTP 401/403/409 等配置或数据错误会保留队列并暂停姿态采样/上传；检查后重新打开 App。卸载/清除应用数据会删除设备队列。

网页提供最近 100 个会话选择、自动跟随最新会话、三维旋转/缩放、XZ 俯视、时间滑块回看、头部方向三轴、位置/角度/四元数读数和完整会话 JSON 导出。旧会话仍保存在数据库。浏览器增量读取采样，保持原图与标签查看功能；“自动刷新”同时控制两个视图。`最近采样距今` 使用设备 UTC 时间，需电脑和设备时钟一致。编辑器数据明确标注，不作为真实空间追踪精度的证据。

## 标签位置的定义

本版本把标签放在**拍照瞬间照片中心射线前方 2 米**，不是返回结果时的视线中心。照片本身没有足够信息决定物体距离，因此这里明确使用固定深度；后续可替换为空间网格求交或实际深度。

真机使用 `PhotoCaptureFrame` 的拍照时相机位姿和投影矩阵，把照片归一化坐标 `(0.5, 0.5)` 转成世界射线。上传前创建 `WorldAnchor`；电脑返回后只填写文字。标签文字可以转向观看者，锚点位置不跟随头部。追踪暂时不可用时隐藏标签，恢复后再显示。

每次触发只拍摄一张图片，不持续上传视频、不逐帧推理。新版已完成标签及可用的空间锚点会归档到电脑，之后可以恢复；待上传照片的锚点按上述队列机制尝试跨会话保存。标签不会跟随被拍物体移动。默认同时显示最多 12 个已完成标签，电脑保留其余历史记录。

## 文件与配置

| 路径 | 用途 |
|---|---|
| `Assets/CaptureDemo/Scenes/CaptureDemo.unity` | 主场景 |
| `Assets/CaptureDemo/Scripts/CaptureController.cs` | Select 按下拍照、相机切换、上传、返回值校验 |
| `Assets/CaptureDemo/Scripts/CapturePressGate.cs` | 按住去重、松开重置、忙碌时不积压手势 |
| `Assets/CaptureDemo/Scripts/CameraCaptureLease.cs` | 自动暂停/恢复直播、相机释放等待、协调超时 |
| `Assets/CaptureDemo/Scripts/CaptureGeometry.cs` | 照片像素射线到世界坐标 |
| `Assets/CaptureDemo/Scripts/AnchoredLabel.cs` | WorldAnchor 与文字显示 |
| `Assets/CaptureDemo/Scripts/NetworkStatusHud.cs` | 左上角 Wi-Fi / IPv4 常驻显示及网络变化刷新 |
| `Assets/CaptureDemo/Scripts/PendingPhotoStore.cs` | 有容量限制、保留请求 ID 的磁盘待上传队列 |
| `Assets/CaptureDemo/Scripts/CaptureUploadQueue.cs` | 2 秒健康检查、串行补传、幂等重试与错误暂停 |
| `Assets/CaptureDemo/Scripts/PoseTelemetry.cs` | 1 Hz 头部位姿采样、会话原点、追踪状态和姿态上传 |
| `Assets/CaptureDemo/Scripts/PoseOutbox.cs` | 姿态磁盘暂存、分段补传与确认后清理 |
| `Assets/CaptureDemo/Scripts/WorkHistoryClient.cs` | 工作身份、空间备份、恢复、设备指令回执与缓存清理 |
| `Assets/CaptureDemo/Editor/DemoBuild.cs` | HoloLens 1 设置、场景创建、构建 |
| `server/server.py` | 三平台共用的 Python 接收服务、配置生成与图像处理入口 |
| `RunServer.cmd` / `RunServer.sh` | 独立前台启动，不依赖 Unity 工具链 |
| `OpenSystemLive.cmd` | Windows 系统直播控制页入口，可传 `-DeviceIp` |
| `config/server.example.json` | 不含真实配对密钥的服务端配置说明 |
| `server/web/` | 本地浏览器查看页，无前端依赖或外部 CDN |
| `server/web/history.js` | 工作/设备选择、恢复与按类别清理缓存 |
| `server/web/system-live.*` | 系统直播内嵌播放器、画质、全息内容与自动拍照切换 |
| `server/data/images/` | 实际收到的 JPEG |
| `server/data/captures.sqlite3` | 持久序列号、请求 ID、图片摘要及每次请求的响应、耗时和发送状态 |
| `server/data/poses.sqlite3` | 独立会话、姿态采样与接收时间 |
| `server/data/work.sqlite3` | 独立的工作、标签、空间锚点备份、设备状态、指令与回执 |
| `logs/` | Unity、服务端、构建及联调记录 |

`Assets/StreamingAssets/demo-config.json` 是本地客户端配置，`config/server.local.json` 是服务端配置；两者含配对密钥，已加入 `.gitignore`。`config/demo-config.example.json` 可作为模板。不要将密钥或含密钥的构建包发到公开仓库。

Git 仓库位于本工程目录，保留 Unity 源码与对应 `.meta`、场景、`Packages`（含锁文件）、`ProjectSettings`、接收服务、网页、脚本和测试。构建包、签名证书、缓存、日志、上传照片、姿态数据库及本机配置均不提交；它们仍保留在本机。同级 `HoloLens1Environment` 是外部工具链，不在本仓库内。新检出工程后，若要进行 Unity 构建，先按上述相对位置准备共享环境，再运行 `Configure-Lan.cmd` 生成本机配置；仅运行服务则使用前文独立入口。首次生成配置不会带入原机器的照片或历史记录，已有设备应复制原配对配置。

可调整客户端 `distanceMeters`（0.5–5 米）、`maxLabels`、`requestTimeoutSeconds`、`maxQueuedCaptures`（默认 20）、`maxQueueMegabytes`（默认 64）；服务端 `delaySeconds` 默认 `1.0`。修改客户端配置后需要重新构建 UWP 应用。本机现有接收服务仍运行于 Windows；服务端也可独立运行在 Linux / macOS。WSL 测试可验证 Linux 代码，头显访问 WSL 则还取决于 WSL 的网络模式和端口可达性，不等同于部署到原生 Linux 局域网电脑。

## 接口与后续接推理

`GET /health` 返回服务状态。`POST /api/captures` 接收原始 JPEG 字节，最大 8 MiB，要求 `Content-Type: image/jpeg`、`Authorization: Bearer <本地密钥>`、`X-Capture-Id: <32 位十六进制请求 ID>`。

`POST /api/poses` 使用相同配对密钥，接收 `{session: {sessionId, startedAt, deviceName, clientKind}, samples: [...]}` JSON；每批 1–60 点、最大 96 KiB，返回 `sessionId`、`acceptedThrough`、`inserted`。姿态按会话与采样序号去重，数据冲突返回 409。查看接口仅供本机浏览器：`GET /api/pose-sessions` 列出最近 100 个会话；`GET /api/pose-sessions/<id>?after=-1&limit=1000` 按序号增量分页返回采样，最多每页 2000 点。

姿态查看接口包含 `historyRevision`。清理电脑轨迹后此值递增，所有查看页会丢弃旧的本地绘图点，即使清理后同一 App 会话继续上报也不会把旧点重新拼回轨迹。

本机查看接口：`GET /api/captures?limit=50&before=<序号>&q=<搜索词>` 返回历史列表与统计；`GET /api/captures/<请求ID>` 返回图片详情和最近 50 次请求；`GET /api/captures/<请求ID>/image` 返回原图，追加 `?download=1` 下载。列表按序号倒序分页，使用返回的 `nextBefore` 读取更早记录。

本机清除接口：`DELETE /api/captures`，必须带 `X-Demo-Action: clear-history`；上传处理中返回 409。成功后返回清除数量及 `historyRevision`，列表也包含该版本号，供其他页面丢弃旧缓存。SQLite 序列计数保留。图片先暂存，记录提交后删除；中途失败会恢复图片，服务重启时也会处理未完成的清除。

工作与缓存接口：

| 方法与路径 | 权限与用途 |
|---|---|
| `POST /api/device-poll` | 设备密钥；注册设备、报告缓存和工作状态、获取最早未完成指令 |
| `POST /api/device-acks` | 设备密钥；提交指令执行结果，重复回执幂等 |
| `POST /api/work-anchors/<captureId>` | 设备密钥；`application/octet-stream`，另带 `X-Device-Id`、`X-Work-Id`、`X-Anchor-Kind`；图片上传成功后归档，校验身份和字节摘要 |
| `GET /api/device-history/<workId>?limit=12` | 设备密钥；获取最近标签清单，最多 30 条 |
| `GET /api/device-anchor/<captureId>` | 设备密钥；下载指定空间备份字节 |
| `GET /api/work-history` | 本机浏览器；工作、设备状态与电脑缓存统计 |
| `POST /api/device-commands` | 本机浏览器 + `X-Demo-Action: device-command`；发送 `resume`、`new_work`、`clear_cache`，使用唯一 `commandId` 幂等去重 |
| `POST /api/cache/clear` | 本机浏览器 + `X-Demo-Action: cache-clear`；`categories` 选择 `images`、`poses`，保留工作归档 |

设备清理指令的 `categories` 为 `downloadedAnchors`、`visibleLabels`、`pendingPhotos`、`pendingPoses` 的非空子集。设备私有接口均使用原 `Authorization: Bearer <本地密钥>`；密钥不下发到网页。指令状态包括 `queued`、`completed`、`partial`、`failed`、`interrupted`、`expired`。`completed` 指操作完成；恢复操作的定位数量还需结合设备状态判断。

成功响应示例：

```json
{"captureId":"00000000000000000000000000000001","serial":"HL1-000001","centerX":0.5,"centerY":0.5,"processingMs":1001}
```

响应还包含图片字节数、SHA-256、接收时间和是否缓存。相同请求 ID 和相同图片可幂等重试；相同 ID 携带不同图片返回 409。序列号写入 SQLite，重启接收服务后继续递增。

目前在服务端保存图片之后 `sleep(delaySeconds)`。将这一步替换为模型调用即可接入推理。当前客户端刻意只接受中心点 `(0.5, 0.5)`；要支持任意检测框中心，需要同时修改对应的射线计算及结果校验，不能只改服务端坐标。

## 已验证与待验证

合拢拍照与直播切换：**46 项 Python 测试在 Windows 原生 Python 与 WSL/Linux 上均通过**，记录见 `logs/camera-handoff-all-tests-{windows,linux}.log`。Unity `CaptureSmoke` 验证了按下触发、按住/忙碌去重、暂停确认前不拍照、松开再拍、暂停超时释放；`HistorySmoke` 回归也通过。Edge 使用独立接收器验证真实跨来源窗口的暂停/恢复，并等开始按钮的临时用户激活过期后才触发交接，确认无需再次点击；另验证停止不会复活窗口、拍照时更换播放窗口不会抢占相机、协调断开时关闭直播。记录见 `logs/capture-smoke.json`、`logs/camera-handoff-browser-smoke.json`。浏览器测试用本机不同来源代替真实视频，不能代替 HoloLens 摄像头的交接验收。 本次另已完成上述真机拍照与直播恢复验证；尚未测量直播延迟、长期稳定性或锚点重定位精度。

重复集成检查（关闭本工程 Unity）：`powershell -ExecutionPolicy Bypass -File scripts\Test-WorkHistory.ps1 -CaptureInteraction`；浏览器自动化：`powershell -ExecutionPolicy Bypass -File scripts\Test-CameraHandoffBrowser.ps1`。均使用隔离的临时数据。正式服务升级前后原有 **20 张 JPEG** 逐一校验一致，未清理正式缓存，见 `logs/camera-handoff-server-update.json`。

工作历史与缓存管理：**38 项 Python 测试在 Windows 原生 Python 与 WSL/Linux 上均通过**，覆盖归档持久化、空间字节校验、幂等重试、指令回执、在线/过期限制、按类别清理与原有接口兼容，记录在 `logs/history-tests-{windows,linux}.log`。Unity `HistorySmoke` 验证了拍照归档、重启恢复、清理下载与显示缓存后重新下载、切换工作、断网队列的显式清理；Edge 验证了默认不选、取消、丢失确认、设备回执、桌面与窄屏布局。记录见 `logs/history-smoke.json`、`logs/history-browser-smoke.json`。所有清理测试使用独立临时数据；正式服务原有 **20 张 JPEG** 已逐个校验未改变，见 `logs/history-server-update.json`。

`1.0.9.0` 的 **UWP / Release / x86 / IL2CPP** 编译与签名已通过，真实 `WorldAnchorTransferBatch` 分支完成编译；包内摄像头模式、配对配置、版本和权限已核对，记录见 `logs/history-package-verification.json`。该次 1.0.9.0 未单独部署；工作历史功能现已随 1.0.10.0 安装。**真实空间锚点导出、重启后的重新定位、原区域标签位置与漂移仍需佩戴头显验证**。编辑器恢复相同模拟坐标不能代替真机定位验收。macOS 尚未执行实机测试。

姿态功能：24 项后端测试已在 WSL 和 Windows 原生 Python 上通过，覆盖采样校验、四元数、认证、本机查看限制、幂等/冲突、会话区分、分页、服务重启保留和清除图片不影响轨迹。Unity `PoseSmoke` 实测采样间隔 **0.998–1.002 秒**；验证相对起点坐标/朝向、队列容量和磁盘重载、2 秒离线探测、响应丢失后补传去重、挂起后断开轨迹，以及新会话重设原点。Edge 已验证三维/俯视切换、回看、会话切换、自动/手动刷新、无刷新按钮闪动、JSON 导出和窄屏布局。记录见 `logs/pose-smoke.json`、`logs/pose-browser-smoke.json`、`logs/pose-server-tests-*.log`，截图为 `logs/trajectory-desktop.png` 和 `logs/trajectory-mobile.png`。截图包含独立测试接收器的模拟数据，未写入正式数据库。

当前最新真机签名包为 `builds/UWP-Signed/HoloLensCaptureDemo_1.0.11.0_Win32.appx`，已通过 USB 安装并核实同一版本；当前网络与真机验证状态见上文。此前姿态功能的两种模式包已核对，记录见 `logs/pose-package-verification.json`。旧模拟器包 `builds/UWP-Simulated-Signed/HoloLensCaptureDemo_1.0.6.0_Win32.appx` 使用此前的电脑地址，当前网络下需要重新构建模拟器版再部署。电脑接收服务此前更新保留了原有 14 张图片，见 `logs/pose-server-update.json`；10 月 31 日的编辑器验证又增加两张测试图。实体头部追踪精度、锚点漂移和设备端姿态队列恢复仍需上机验收。

离线队列：存储检查已验证容量限制、JPEG 完整性、FIFO 恢复、请求 ID 保留和已确认上传的清理。Unity `OfflineSmoke` 在独立接收器上模拟断网、恢复以及响应丢失：最终两次探测间隔约 2.00 秒 / 2.01 秒；离线期间没有 POST；恢复后两张照片、三次上传尝试只得到两个序列号，两个标签均保留拍照时位置。记录见 `logs/offline-smoke.json` 和 `logs/offline-smoke-events.jsonl`。正常在线 `Smoke` 也已重新通过。

离线功能安装包已完成 **UWP / Release / x86 / IL2CPP** 构建并签名：模拟器版 `builds/UWP-Simulated-Signed/HoloLensCaptureDemo_1.0.4.0_Win32.appx`，真实摄像头版 `builds/UWP-Signed/HoloLensCaptureDemo_1.0.5.0_Win32.appx`。包内模式、队列容量、代码与版本已核对，见 `logs/offline-package-verification.json`。导出脚本会同步增量导出保留的 manifest 版本，便于直接升级并保留应用数据。

本次模拟器启动的 Windows UAC 被取消，故新版本尚未部署到模拟器；设备上退出应用再重开后的队列和空间锚点恢复尚待实测。已有的模拟器截图和下方联调记录属于此前版本，不能代替本次离线功能的设备验收。不要卸载旧应用来升级，否则会丢失其私有存储和待上传照片。

清除记录与刷新修正：20 项接收服务测试覆盖清除范围、访问限制、上传并发、文件占用回滚和中断恢复。Edge 在独立的 `127.0.0.1:8766` 测试服务中验证了确认、取消、处理中拒绝清除、多页同步、清除后上传和手动刷新；连续自动轮询期间“立即刷新”按钮属性变化为 0。记录见 `logs/clear-history-tests-*.log` 与 `logs/clear-browser-smoke.json`。测试没有删除正式接收服务的图片。

2026-10-30 更新：浏览器监控接收服务的 **13 项测试**在 WSL 和 Windows 上均通过，涵盖旧记录、分页搜索、原图下载、处理中状态、幂等重试、精确响应保存、断连及服务中断恢复。Edge 已实测模拟器上传自动刷新、中心标签叠加、历史记录保持选择、暂停刷新时搜索，以及窄屏布局；记录见 `logs/monitor-tests-*.log`、`logs/monitor-browser-smoke.json` 和 `logs/monitor-desktop.png`。

网络信息栏已通过 Unity 编辑器位姿测试，移动相机后仍位于左上角；真实摄像头与模拟图片两种 UWP x86 包均已完成编译。`1.0.1.0` 模拟器包已安装运行，左上角显示的 IPv4 与 Device Portal 的虚拟网卡地址一致，截图见 `logs/emulator-network-hud.png`。模拟器无法验证真实 Wi-Fi SSID 及真机网络切换。

2026-10-30：接收服务在 WSL 和 Windows 原生 Python 上的 6 项测试均通过；修正了 Windows 下 SQLite 连接未及时关闭的问题。Windows Unity 编辑器完成两次实际 HTTP 图片上传，返回不同序列号，约 1 秒延迟，等待期间移动相机后两个标签位置保持不变；真实摄像头模式和模拟图片模式的 **UWP / Release / x86 / IL2CPP** 均编译成功，0 错误（Unity 生成代码有 1 条弃用 API 警告）。记录见 `logs/editor-smoke.json`、`logs/build-status.json`、`logs/build-status-simulated.json`、`logs/server-tests-windows.log` 和 `logs/msbuild*.log`。两种包内的摄像头模式和所需权限均已核对。

**HoloLens 1 模拟器端联调也已通过**：主机重启后自动发现新 IP、重复部署、启动 UWP 应用、模拟 Air Tap 触发实际上传，服务端约 1010 ms 后返回 `HL1-000004`；WorldAnchor 已定位并显示文字。模拟向右移动后，标签在画面中向左移，中心准星与提示文字保持原位，符合世界空间固定的效果。记录见 `logs/emulator-deployment.json`、`logs/emulator-interaction.json`，对比图为 `logs/emulator-before-move.png` 和 `logs/emulator-after-move.png`。

已完成实体 HoloLens 的 USB 安装；实际运行联调状态记录在 `logs/usb-deployment.json` 及后续真机验证日志中。实际摄像头、Air Tap、真机照片位姿与空间锚点稳定性需按各项实测结果验收，不能用编辑器位姿测试代替真实设备空间追踪测试。

可重复检查：`python -m unittest discover -s tests -v`；Windows 集成测试：关闭本工程 Unity，确保接收服务运行，再执行 `powershell -ExecutionPolicy Bypass -File scripts\Build-Demo.ps1 -Stage Smoke`。

离线队列测试：关闭本工程 Unity，执行 `powershell -ExecutionPolicy Bypass -File scripts\Test-OfflineQueue.ps1`。脚本在本机 8767 端口启动独立接收器，模拟断连与响应丢失，结束后关闭并清理测试数据；不停止正式接收服务，也不改动已有照片。

姿态与浏览器集成测试：关闭本工程 Unity，执行 `powershell -ExecutionPolicy Bypass -File scripts\Test-PoseTelemetry.ps1 -Browser`。使用独立的本机 8768 端口与临时数据库，完成后自动关闭测试接收器和测试用 Edge。省略 `-Browser` 可只检查 Unity 姿态链路。

工作历史与缓存集成测试：关闭本工程 Unity，执行 `powershell -ExecutionPolicy Bypass -File scripts\Test-WorkHistory.ps1 -Browser`。使用独立的本机 8769 端口与临时数据库；清理操作仅作用于测试设备身份和临时接收器。浏览器检查还会比较正式服务照片数量，因此加 `-Browser` 时需要正式服务也在运行。测试不会清理正式设备或电脑历史。
