电脑 WiFi 热点 —— 使用说明
单文件程序：WiFi热点.exe（双击即用，首次运行会弹 UAC 请求管理员权限，这是必须的）。
1. 快速上手
1. 双击 WiFi热点.exe，在 UAC 弹窗点「是」。
2. 程序会自动检测本机热点能力，并填好默认网络名称（Hotspot-<计算机名>）和随机 16 位密码。
3. 需要改名称/密码就直接改文本框（密码 8–63 位），密码可点「显示」核对，也可点「复制 SSID」。
4. 点「启动热点」→ 状态变绿显示「热点运行中」，用手机搜索该名称、输入密码即可连接。
5. 已连设备数量与设备名、IP 会显示在右侧列表，每 3 秒刷新。
6. 点「停止热点」关闭。
界面上的设置会自动保存，下次打开沿用（配置文件：C:\ProgramData\WiFiHotspot\settings.ini）。
2. 功能与实现方式
功能
实现
自定义名称
1–32 字节（中文约 10 个字）
密码可修改
8–63 位；运行中改完再点「启动热点」，程序自动停止→按新配置重启应用
不显示名称
见第 4 节（Windows 平台的真实能力边界：手机仍会看到一个空白条目）
频段选择
自动 / 2.4GHz / 5GHz（驱动不支持 5GHz 时会自动回落到默认频段）
共享上网源
默认「自动（当前联网连接）」，也可指定某块网卡/某个连接
已连设备
名称 + IP，上限一般 8 台（取决于驱动）
环境自检
一键输出系统版本、管理员权限、WlanSvc/SharedAccess 服务、无线网卡、驱动承载网络能力、热点能力枚举与修复建议
双引擎（网卡兼容性的关键）
1. 现代移动热点（默认）：调用 Windows 原生 NetworkOperatorTetheringManager，就是「设置 → 移动热点」用的同一套引擎，走 Wi-Fi Direct 虚拟网卡，网卡兼容性等同于系统自带热点，无需手动配置共享（系统自动做 ICS）。
2. 兼容模式（旧版托管网络）：勾选后改用 netsh wlan set hostednetwork + 自动配置 ICS 共享（HNetCfg COM）。适用于老网卡/老驱动，以及现代热点 API 被系统策略禁用的情况（如部分 Windows Server SKU、组策略限制）。
程序启动时会查询热点能力枚举（Enabled / DisabledBySku / DisabledByHardwareLimitation / DisabledByGroupPolicy …）：
- 结果为 Enabled → 用现代引擎；
- 其他结果 → 自动勾选「兼容模式」并提示原因。
说明：Intel AX200/AX211 这类新网卡的驱动已移除「承载网络」能力（netsh wlan show drivers 显示「支持的承载网络: 否」），但现代引擎照常可用——本机实测 AX211 启动成功。
3. Windows Server 2022 部署要点
Server 版默认没有安装无线组件，按以下顺序处理：
1. 以管理员打开 PowerShell，安装无线 LAN 服务：
  Install-WindowsFeature Wireless-Networking
  安装后按提示重启。
2. 安装无线网卡厂商驱动（Server 版务必装厂商原版驱动；很多桌面版驱动包会跳过 Server）。
3. 确认服务：WlanSvc（无线自动配置）、SharedAccess（Internet 连接共享）存在。
4. 打开 WiFi热点.exe，点「环境自检」确认：
  - 无线网卡数量 ≥ 1；
  - 移动热点能力为 Enabled（不是 DisabledBySku），否则程序会自动走兼容模式；
  - 兼容模式下要求驱动「支持的承载网络: 是」。
5. 若两个引擎都不可用，自检报告会直接给出原因（无网卡 / 未装无线服务 / 驱动不支持 / SKU 限制）。
Server 2022 常见结果：现代热点 API 返回 DisabledBySku，此时只要无线网卡驱动支持承载网络，兼容模式（netsh + ICS）即可正常工作；不支持就换一个支持软 AP 的 USB 无线网卡（如 RTL8812BU/8821CU 等芯片方案）。
4. 关于「不显示名称」的真实能力边界（必看）
结论：Windows 自建热点做不到路由器那种「手机完全搜不到」。 本工具的这一项只是把名字变成不可见，手机 Wi-Fi 列表里仍会出现一个「有锁、无名字」的空白条目（实际扫描截图就是这样）。
技术上为什么做不到：
1. 路由器式隐藏只改 beacon：beacon 里的 SSID 字段长度写 0，probe response 里仍回真名，客户端手动输入名字才能连。
2. SSID 参与 WPA2 四次握手的密钥派生（PTK = f(PMK, ANonce, SNonce, AP MAC, STA MAC, SSID)），所以 AP 必须真的“拥有”这个名字。既不可能“广播完全空、又能被连上”，也不能靠伪造空名绕过。
3. Windows 用户态没有 beacon 控制权：beacon 由网卡驱动/WlanSvc 生成，API 只接受 1–32 字节的非空 SSID，没有“关闭广播”开关。实测排除过的路径：
  - netsh wlan set hostednetwork 只有 mode / ssid / key / keyusage，网传的 option=hide 不存在（直接报参数错误）；
  - 移动热点 API 的配置对象只有 Ssid / Passphrase / Band / AuthenticationKind；
  - 网卡驱动高级属性（AX211 实测）无任何隐藏/软 AP 广播开关；
  - Wi-Fi Direct WiFiDirectAdvertisementPublisher 只有 ListenStateDiscoverability / IsAutonomousGroupOwnerEnabled / LegacySettings，只影响 WFD 自身探测应答，legacy AP 的 beacon 照样带 SSID。
所以本工具的做法只能是：SSID 用不可见字符（U+200B），手机渲染为空名条目。客户端要连就在「手动添加网络 / 添加其他网络」里粘贴本工具「复制 SSID」给出的名称（粘贴后输入框看起来是空的，正常）。
真要「扫不到」，只有三条路：① 路由器/随身 Wi-Fi 开隐藏 SSID；② 自带 NDIS 驱动的软 AP 方案（第三方工具，需单独安装，Windows 原生引擎不行）；③ 自己写带签名的虚拟无线驱动（不现实）。
5. 常见问题
现象
原因 / 处理
双击没反应 / 弹 UAC 后无窗口
必须以管理员运行；被杀软拦截时加白名单
启动失败，日志「StartTethering did not reach On state」
没有可用的联网连接（热点引擎需要一个连接配置文件）；先连 Wi-Fi 或有线，或用 KM-TEST 回环适配器制造一个连接
显示能力 DisabledBySku
Server/精简版系统限制，自动转兼容模式；兼容模式也失败则见第 3 节
兼容模式报「承载网络启动失败」
网卡驱动不支持软 AP（netsh wlan show drivers → 支持的承载网络: 否）；换驱动或换网卡
手机能连上但上不了网
兼容模式下 ICS 未生效：网络连接面板里右键热点虚拟网卡 → 属性 → 共享 → 勾选「允许其他网络用户…」并选择上网网卡（现代引擎一般不需要手动做）
5GHz 勾选后仍是 2.4GHz
该驱动/区域不支持 5GHz 热点，程序自动回落
状态一直是「切换中…」
驱动正在建 AP，等 10–30 秒；超时后点停止再启动
程序日志区会记录每一步（能力查询、启动结果、ICS 配置结果），排查时先看日志。
6. 源码与重新编译
src\ 目录内含全部源码，无需 Visual Studio：
- WiFiHotspot.cs：WinForms 主程序（C# 5 语法，兼容 .NET Framework 4.x）
- HotspotPs.ps1：内置的 PowerShell 桥（以资源方式嵌进 exe，负责 WinRT 热点调用与 ICS 配置）
- app.manifest：requireAdministrator + 高 DPI + 系统兼容性清单
- app.ico：程序图标
- build.ps1：调用系统自带 csc.exe 编译，输出 ..\WiFi热点.exe
重新编译：在 PowerShell 里执行
& "C:\Users\changzhaohui\Doubao\chats\2026-11-23\new-chat\src\build.ps1"
运行依赖：Windows 7 SP1 及以上/.NET Framework 4.x（Windows 10/11、Windows Server 2016/2019/2022 均自带；现代引擎需 Windows 10 1607+）。程序自身只依赖系统组件，无第三方 DLL。
7. 已验证项（本机 Windows 11 + Intel AX211 实测）
- 双击运行、界面渲染、管理员清单嵌入：正常
- 能力查询 Enabled、启动热点、状态 On、客户端计数（上限 8）、停止热点：正常
- 通过界面按钮启动/停止（含日志输出）：正常
- 自定义 SSID（含中文/空格）与密码写入生效：正常
- 不显示名称（零宽名称）：热点能以不可见名称启动成功；实测手机侧会显示为空白条目（即第 4 节所述边界）
- 运行状态区布局：引擎/频段/已连设备/当前 SSID 显示正常（已修复早期版本文字压到设备列表、数值不显示的问题）
- 运行状态区「网卡」一行（新增，只读）：显示当前启用的无线网卡（已排除 Wi-Fi Direct 虚拟适配器）与承载上网的连接；鼠标悬停可看完整型号。多网卡时仅提示，不自动切换
- 兼容模式（旧版托管网络 + ICS）：代码路径完整、错误信息可回显；本机 AX211 驱动不支持承载网络，未能在本机验证成功路径（需老网卡/支持软 AP 的网卡或 Server + 支持驱动）
