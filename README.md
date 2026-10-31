# HoloLensCaptureDemo

HoloLens 1 单次拍照、电脑接收并返回序列号、世界空间标签的最小 Unity Demo。

工程目录：`C:\Users\Thinkpad\Documents\HololensWorkspace\HoloLensCaptureDemo`。Windows 上的 Unity 构建及部署工具链在同级 `HoloLens1Environment`，Unity 固定为 **2019.4.41f2**；使用这些构建脚本时需保留两个目录的相对位置。独立运行接收服务和 Web 不需要 Unity 或该工具链。

## Windows / Linux / macOS 共用服务端

三个平台运行同一份 `server/server.py` 和 `server/web/`，只需 **Python 3.11+（包含 sqlite3）**，当前 Demo 无第三方 Python 包、Node.js 或数据库服务依赖。路径使用 Python 的跨平台 [`pathlib`](https://docs.python.org/3/library/pathlib.html)，照片和轨迹保存在本地 SQLite 与 JPEG 文件中。精简 Python 发行版需确认包含 [`sqlite3`](https://docs.python.org/3/library/sqlite3.html)。

| 部分 | 运行位置 |
|---|---|
| 拍照、头部追踪、空间锚点、显示返回结果 | HoloLens 1 |
| 接收照片、图像处理入口、响应、离线补传接收、姿态存储 | Windows / Linux / macOS，同一套 Python 代码 |
| 照片、结果、轨迹的 Web 查看页面 | 三个平台的浏览器，同一套 HTML / CSS / JavaScript |
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
2. 将当前的 `config/server.local.json` 私下复制到目标电脑的同一相对位置，保留与头显一致的 `apiToken`；不要重新生成密钥。需要迁移历史照片和轨迹时，先停止原接收服务，再完整复制 `server/data/`。
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

编辑器用程序生成的 JPEG 测试图，网络、图片保存、延迟和序列号均走真实服务。画面会明确显示 `SIMULATED PHOTO`。

当前接收地址为 `http://192.168.100.92:8765`（2026-10-31，Wi-Fi `e1-102`，实体头显为 `192.168.100.61`）。此前地址为 `192.168.0.113`。电脑换网络或 IP 后，关闭 Unity，运行 `Configure-Lan.cmd`，再打开或重新构建应用。该脚本保留已有配对密钥，并可配置仅允许本地子网访问、限定 Python 程序和端口的 Windows 防火墙规则。

## 在浏览器中查看图片与响应

双击 **`OpenMonitor.cmd`**，或在这台电脑的浏览器打开 **<http://127.0.0.1:8765/>**。快捷入口会在需要时自动启动接收服务，无需另外安装网页工具或启动 Unity。

- 每秒自动刷新上传历史，默认跟随最新图片；点击历史记录后保持当前选择，可点击“跟随最新”恢复。
- 查看上传的 JPEG 原图、返回的序列号、图像坐标、处理耗时、来源 IP 和请求 ID；可开关标签叠加预览、下载原图。
- 搜索序列号或请求 ID，查看每次重试的发送状态和完整响应 JSON，支持复制。点击有响应的请求行可查看该次返回值。
- 右上角“清除记录”会先弹出确认框，再删除全部历史记录、对应原图和响应（包括搜索范围之外的记录）。有上传正在进行时会提示稍后重试；清除后序列号继续递增，头显中已显示的标签不受影响。其他查看页在下次刷新时同步移除旧记录。
- 自动刷新在后台进行，不会反复禁用“立即刷新”按钮。关闭自动刷新后，仍可手动刷新。
- 页面区分处理中、已发送响应、发送断连和服务中断。**“已发送响应”仅表示服务端成功向连接写出数据；当前协议没有头显“标签已显示”的回执。**

网页中的标签是按返回坐标叠加在照片上的预览，不能用它判断真机世界锚点是否稳定。页面启用前的图片和序列号仍可查看，但会注明缺少当时的响应明细。相同请求 ID 的重试合并在同一图片记录中。

查看页及图片查询接口仅限接收电脑本机访问，无需在浏览器输入配对密钥。头显仍通过原有局域网上传接口发送图片。关闭网页不影响接收，关闭服务后数据仍保存在 `server/data/`；没有自动删除历史图片的机制。

## 在 HoloLens 1 上运行

2026-10-31 已通过 USB 在实体设备 `HoloLens-7V9C5` 上安装并升级到 **1.0.8.0**，包含拍照、照片离线队列、网络 HUD 与 1 Hz 姿态轨迹。本次使用 `update` 保留应用数据，修正电脑接收地址并增加明确的中文拍照提示。版本核实结果见 `logs/usb-deployment.json`；第一次拍照时需在头显中允许摄像头权限。USB 用于部署，现有图片和姿态上传仍通过 Wi-Fi 进行。编译和编辑器两次实际 HTTP 上传测试已通过，升级后的真机拍照/姿态接收等待佩戴设备验证。

后续 USB 升级可运行 `DeployUsb.cmd -DeviceId 34d02739-c8d0-45bd-119f-54ea5fbd3d97`。脚本检查最新已签名真机包及接收密钥，已安装时使用 `update` 保留应用数据，安装后再次查询核实版本。首次连接新设备时，按 Windows SDK 提示在头显开发者选项点击 Pair，并添加 `-Pin <临时配对码>`；配对成功后通常不需重复输入。脚本不会卸载已有应用。

1. 电脑运行 `StartServer.cmd`，头显连接同一局域网。
2. 关闭本工程的 Unity 窗口，运行 `BuildDemo.cmd`。
3. 用共享环境中的 Visual Studio 2019 打开 `builds\UWP\HoloLensCaptureDemo.sln`，选择 **Release / x86 / Remote Machine**，填写头显 IP，并按 VS 的配对提示部署运行。头显需开启开发人员模式。
4. 允许摄像头权限。看向要标记的位置，做一次 **Air Tap（空中点击）**。
5. 应用拍一张不含全息内容的照片，上传到电脑；收到返回值后显示序列号。转头、走动，检查标签仍留在原位置。

**Demo 的拍照入口：**打开 `HoloLensCaptureDemo`，看到准星、网络 HUD 与“拍照并上传：空中点击一次，无需打开系统菜单”后，直接做一次 Air Tap，即进入拍照、保存和上传流程，不会再弹出手势教学或第二次确认。摄像头首次授权是系统权限步骤。

**系统相机与 Demo 是两个入口：**在沉浸式 App 内张手（Bloom）出现的五个图标属于 HoloLens 系统快捷菜单；其中“拍照”进入系统混合现实相机，将照片保存至系统相册，不会调用 Demo 的上传接口。这个系统拍照入口及其手势提示不能由 Demo 改写。需要上传照片、返回序列号并生成世界标签时，使用上述 Demo 的一次 Air Tap 操作。参见微软的[系统拍照说明](https://learn.microsoft.com/en-us/hololens/holographic-photos-and-videos)。

编译脚本产出的是用于验证编译的**未签名**包。每次 Unity 导出自动递增 UWP 包版本，避免修改内容后仍使用相同版本而被系统拒绝。`scripts\Sign-Demo.ps1` 签名最近构建的包并生成 APPX，或者使用 VS 的开发部署。签名脚本将不可导出的私钥保存在 Windows 当前用户的证书存储中，输出公共 `.cer`，不会修改宿主机的受信任根证书。

`BuildSimulatedDemo.cmd` 生成 `builds\UWP-Simulated`，供模拟器使用程序生成的照片。这个版本明确标记模拟模式；真机版本不会在拍照失败时偷偷改用测试图。模拟器的安装与启动说明见同级环境的 `docs\EMULATOR.md`。

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

头显使用**触发式拍照 + 1 Hz 姿态上报**，没有持续视频流，也不按固定频率查询标签。每次 Air Tap / Space 拍摄一张 JPEG，先写入设备队列，再尝试连接电脑；电脑通过同一次 HTTP POST 的响应返回标签。网页每秒刷新的请求只发生在浏览器与电脑本机服务之间。下面的状态表仅描述照片队列；没有照片时，前台运行的 App 仍会发送姿态数据。

| 状态 | 头显的网络行为 |
|---|---|
| 无待上传照片 | 不探测电脑，不上传 |
| 新的一批照片 | 先检查一次 `GET /health`，成功后串行 `POST /api/captures` |
| 电脑不可达且队列非空 | 每 2 秒启动一次健康检查，单次探测超时 2 秒，不并发探测、不反复发送大图 |
| 网络恢复 | 按入队顺序补传，一张收到并验证响应后才处理下一张 |
| 上传中断或 408 / 429 / 5xx | 保留照片，重新进入 2 秒探测；每次重新完整上传该 JPEG |
| 其他 HTTP 错误或响应异常 | 保留数据并暂停，HUD 显示原因；修正电脑配置后 Air Tap / R 重试队列 |

健康检查只访问 `serverUrl` 配置的电脑，不扫描局域网、不自动发现变更后的 IP。正在执行的图片上传仍使用 `requestTimeoutSeconds`（默认 15 秒），超时后才进入重连检查。因此断线发生在上传过程中时，首次恢复检测可能先经历这段超时。

队列默认最多 **20 张 / 64 MiB 图片数据**，任一限制达到就暂停新增拍照，提示保留旧照片等待电脑。UWP 队列位于设备应用私有存储 `LocalState\CaptureOutbox`；编辑器队列位于工程 `logs\editor-outbox`。JPEG 与元数据完整写入后才成为正式队列项；收到合法响应后才移出队列。每张照片的 `captureId` 固定，响应丢失后的重试仍沿用它，利用接收服务的幂等机制避免重复序列号。

在应用前台运行时，断线后可以继续拍照，上传工作不占用拍照按钮。退出或系统挂起期间不会继续轮询；重新打开应用会读取本地队列继续处理。重启同一应用会保留队列，卸载应用或清除应用数据则不会。电脑网页的“清除记录”不会删除设备队列，恢复连接后暂存照片仍会上传。

待上传照片会尝试将拍照时的 `WorldAnchor` 保存到设备的 [WorldAnchorStore](https://learn.microsoft.com/en-us/windows/mixed-reality/develop/unity/spatial-anchors-in-unity)。重开应用后先加载锚点，再填写补传返回的标签；暂时未定位时隐藏文字，等待空间追踪恢复。如果锚点保存或恢复失败，照片仍可上传并在网页查看，应用不会把旧标签放到新的视线前方。

局域网需要头显与电脑能够互通、允许电脑 TCP 8765 入站；当前防火墙规则限定本地子网。不需要公网连接。可为电脑设置 DHCP 地址保留，避免换地址后重建客户端配置。容量估算使用 **JPEG 字节数 × 每秒拍摄次数 × 8**；例如每张 1 MB、每 5 秒一张，平均约 1.6 Mbps，另留协议和重传余量。当前模拟图只有约 14 KiB，不能用它代替真实摄像头照片评估带宽。恢复补传时，每张还要等待服务端默认 1 秒处理，因此实际补传速度低于每秒一张。

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

每次触发只拍摄一张图片，不持续上传视频、不逐帧推理。已完成的标签只在本次运行期间存在；待上传照片的锚点按上述队列机制尝试跨会话保存。标签不会跟随被拍物体移动。默认最多保留 12 个已完成标签。

## 文件与配置

| 路径 | 用途 |
|---|---|
| `Assets/CaptureDemo/Scenes/CaptureDemo.unity` | 主场景 |
| `Assets/CaptureDemo/Scripts/CaptureController.cs` | 手势、拍照、上传、返回值校验 |
| `Assets/CaptureDemo/Scripts/CaptureGeometry.cs` | 照片像素射线到世界坐标 |
| `Assets/CaptureDemo/Scripts/AnchoredLabel.cs` | WorldAnchor 与文字显示 |
| `Assets/CaptureDemo/Scripts/NetworkStatusHud.cs` | 左上角 Wi-Fi / IPv4 常驻显示及网络变化刷新 |
| `Assets/CaptureDemo/Scripts/PendingPhotoStore.cs` | 有容量限制、保留请求 ID 的磁盘待上传队列 |
| `Assets/CaptureDemo/Scripts/CaptureUploadQueue.cs` | 2 秒健康检查、串行补传、幂等重试与错误暂停 |
| `Assets/CaptureDemo/Scripts/PoseTelemetry.cs` | 1 Hz 头部位姿采样、会话原点、追踪状态和姿态上传 |
| `Assets/CaptureDemo/Scripts/PoseOutbox.cs` | 姿态磁盘暂存、分段补传与确认后清理 |
| `Assets/CaptureDemo/Editor/DemoBuild.cs` | HoloLens 1 设置、场景创建、构建 |
| `server/server.py` | 三平台共用的 Python 接收服务、配置生成与图像处理入口 |
| `RunServer.cmd` / `RunServer.sh` | 独立前台启动，不依赖 Unity 工具链 |
| `config/server.example.json` | 不含真实配对密钥的服务端配置说明 |
| `server/web/` | 本地浏览器查看页，无前端依赖或外部 CDN |
| `server/data/images/` | 实际收到的 JPEG |
| `server/data/captures.sqlite3` | 持久序列号、请求 ID、图片摘要及每次请求的响应、耗时和发送状态 |
| `server/data/poses.sqlite3` | 独立会话、姿态采样与接收时间 |
| `logs/` | Unity、服务端、构建及联调记录 |

`Assets/StreamingAssets/demo-config.json` 是本地客户端配置，`config/server.local.json` 是服务端配置；两者含配对密钥，已加入 `.gitignore`。`config/demo-config.example.json` 可作为模板。不要将密钥或含密钥的构建包发到公开仓库。

Git 仓库位于本工程目录，保留 Unity 源码与对应 `.meta`、场景、`Packages`（含锁文件）、`ProjectSettings`、接收服务、网页、脚本和测试。构建包、签名证书、缓存、日志、上传照片、姿态数据库及本机配置均不提交；它们仍保留在本机。同级 `HoloLens1Environment` 是外部工具链，不在本仓库内。新检出工程后，若要进行 Unity 构建，先按上述相对位置准备共享环境，再运行 `Configure-Lan.cmd` 生成本机配置；仅运行服务则使用前文独立入口。首次生成配置不会带入原机器的照片或历史记录，已有设备应复制原配对配置。

可调整客户端 `distanceMeters`（0.5–5 米）、`maxLabels`、`requestTimeoutSeconds`、`maxQueuedCaptures`（默认 20）、`maxQueueMegabytes`（默认 64）；服务端 `delaySeconds` 默认 `1.0`。修改客户端配置后需要重新构建 UWP 应用。本机现有接收服务仍运行于 Windows；服务端也可独立运行在 Linux / macOS。WSL 测试可验证 Linux 代码，头显访问 WSL 则还取决于 WSL 的网络模式和端口可达性，不等同于部署到原生 Linux 局域网电脑。

## 接口与后续接推理

`GET /health` 返回服务状态。`POST /api/captures` 接收原始 JPEG 字节，最大 8 MiB，要求 `Content-Type: image/jpeg`、`Authorization: Bearer <本地密钥>`、`X-Capture-Id: <32 位十六进制请求 ID>`。

`POST /api/poses` 使用相同配对密钥，接收 `{session: {sessionId, startedAt, deviceName, clientKind}, samples: [...]}` JSON；每批 1–60 点、最大 96 KiB，返回 `sessionId`、`acceptedThrough`、`inserted`。姿态按会话与采样序号去重，数据冲突返回 409。查看接口仅供本机浏览器：`GET /api/pose-sessions` 列出最近 100 个会话；`GET /api/pose-sessions/<id>?after=-1&limit=1000` 按序号增量分页返回采样，最多每页 2000 点。

本机查看接口：`GET /api/captures?limit=50&before=<序号>&q=<搜索词>` 返回历史列表与统计；`GET /api/captures/<请求ID>` 返回图片详情和最近 50 次请求；`GET /api/captures/<请求ID>/image` 返回原图，追加 `?download=1` 下载。列表按序号倒序分页，使用返回的 `nextBefore` 读取更早记录。

本机清除接口：`DELETE /api/captures`，必须带 `X-Demo-Action: clear-history`；上传处理中返回 409。成功后返回清除数量及 `historyRevision`，列表也包含该版本号，供其他页面丢弃旧缓存。SQLite 序列计数保留。图片先暂存，记录提交后删除；中途失败会恢复图片，服务重启时也会处理未完成的清除。

成功响应示例：

```json
{"captureId":"00000000000000000000000000000001","serial":"HL1-000001","centerX":0.5,"centerY":0.5,"processingMs":1001}
```

响应还包含图片字节数、SHA-256、接收时间和是否缓存。相同请求 ID 和相同图片可幂等重试；相同 ID 携带不同图片返回 409。序列号写入 SQLite，重启接收服务后继续递增。

目前在服务端保存图片之后 `sleep(delaySeconds)`。将这一步替换为模型调用即可接入推理。当前客户端刻意只接受中心点 `(0.5, 0.5)`；要支持任意检测框中心，需要同时修改对应的射线计算及结果校验，不能只改服务端坐标。

## 已验证与待验证

姿态功能：24 项后端测试已在 WSL 和 Windows 原生 Python 上通过，覆盖采样校验、四元数、认证、本机查看限制、幂等/冲突、会话区分、分页、服务重启保留和清除图片不影响轨迹。Unity `PoseSmoke` 实测采样间隔 **0.998–1.002 秒**；验证相对起点坐标/朝向、队列容量和磁盘重载、2 秒离线探测、响应丢失后补传去重、挂起后断开轨迹，以及新会话重设原点。Edge 已验证三维/俯视切换、回看、会话切换、自动/手动刷新、无刷新按钮闪动、JSON 导出和窄屏布局。记录见 `logs/pose-smoke.json`、`logs/pose-browser-smoke.json`、`logs/pose-server-tests-*.log`，截图为 `logs/trajectory-desktop.png` 和 `logs/trajectory-mobile.png`。截图包含独立测试接收器的模拟数据，未写入正式数据库。

当前最新真机签名包为 `builds/UWP-Signed/HoloLensCaptureDemo_1.0.8.0_Win32.appx`，已通过 USB 升级并查询核实安装；当前网络与真机验证状态见上文。此前姿态功能的两种模式包已核对，记录见 `logs/pose-package-verification.json`。旧模拟器包 `builds/UWP-Simulated-Signed/HoloLensCaptureDemo_1.0.6.0_Win32.appx` 使用此前的电脑地址，当前网络下需要重新构建模拟器版再部署。电脑接收服务此前更新保留了原有 14 张图片，见 `logs/pose-server-update.json`；10 月 31 日的编辑器验证又增加两张测试图。实体头部追踪精度、锚点漂移和设备端姿态队列恢复仍需上机验收。

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
