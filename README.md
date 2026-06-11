# AiCheckBid Next (Tauri + React + Rust + .NET Sidecar)

## 架构

- 前端：React + Vite
- 桌面壳：Tauri
- 主控后端：Rust (`src-tauri`)
- 文档解析 sidecar（主）：.NET Framework 4.8 + Spire (`sidecar/DocParserSidecarNet48`)
- 文档解析 sidecar（兜底）：.NET 8 + OpenXML (`sidecar/DocParserSidecar`)

## 已实现

- React 主界面已复刻核心流程：授权状态、套餐选择、生成激活码、激活、执行检查、查看报告
- React 已支持多文档队列（按授权文档数上限限制）、批量检查、结果目录一键打开
- React 已支持认证模式切换（设备码/U盾盘符）
- Rust 授权模块（本地持久化）：
  - 设备码生成
  - 序列号生成与设备码匹配校验
  - U盾模式校验（盘符存在性检查）
  - 激活码校验与激活状态管理
  - 时间回拨校验（UN 语义）与使用次数令牌（UN1 语义）
  - 套餐页数限制（未激活默认 3 页）
  - 使用次数统计
- OpenXML/net8 规则补齐：检查段前段后、智能修正标记语义、彩色图片检查（Word图片可判定）
- Rust 命令 `parse_document` 采用双轨调用：
  - 优先执行 `net48` sidecar（更接近原版行为）
  - 若不可用或失败，自动回退 `net8/OpenXML` sidecar
- sidecar 返回统一 JSON 协议
- 打通了“前端 -> Rust -> sidecar -> Rust -> 前端”链路

## 运行

1. 安装 Node.js
2. 安装 Rust，并确保使用 `rustup` 管理 MSVC toolchain
3. 安装 `.NET 8 SDK`
4. 安装 Visual Studio Build Tools 或 `.NET Framework 4.8 Developer Pack`
5. 确保 `sidecar/lib/` 下存在 `spire.doc.dll` 与 `spire.license.dll`
6. 在项目根目录执行：

```bash
npm install
npm run tauri dev
```

## 自动化验收

在项目根目录执行以下命令可一键完成“构建 + 双样本回归 + 基线校验 + 产物检查”：

```powershell
& .\scripts\finalize-delivery.ps1
```

仅生成双样本回归统计（不做基线校验）：

```powershell
& .\scripts\run-final-regression.ps1
```

### 构建 net48 sidecar（首次）

```powershell
& "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe" `
  .\sidecar\DocParserSidecarNet48\DocParserSidecarNet48.csproj `
  /t:Build /p:Configuration=Debug /p:Platform=AnyCPU /v:minimal
```

构建后可执行文件：

`sidecar/DocParserSidecarNet48/bin/Debug/DocParserSidecarNet48.exe`

### 交付构建

`scripts/build-delivery.ps1` 会在打包前显式构建并校验：

- `sidecar/DocParserSidecar/bin/Debug/net8.0/DocParserSidecar.exe`
- `sidecar/DocParserSidecarNet48/bin/Debug/DocParserSidecarNet48.exe`

默认执行：

```powershell
& .\scripts\build-delivery.ps1
```

- 默认产出便携版：`delivery/AiCheckBidNext.exe`
- 默认同步规则、sidecar 与结果目录到 `delivery/`
- 默认额外生成便携包压缩文件：`AiCheckBidNext_delivery_latest.zip`

生成安装包：

```powershell
& .\scripts\build-delivery.ps1 -Installer
```

- 安装包输出目标：`delivery/AiCheckBidNext_0.1.0_x64-setup.exe`

已知构建机问题：

- 如果日志中出现 `failed to run custom build command for proc-macro2` 且伴随 `Os { code: 0, message: "操作成功完成。" }`，这是 Windows 构建宿主环境问题，不是业务代码编译错误。
- 可先执行：

```powershell
& .\scripts\repair-build-env.ps1 -ProjectRoot 'd:\work\ollama\AiCheckBid_tauri'
```

## 说明

- 当前“套餐购买”已接入为本地模拟支付流程（生成可激活注册码），用于完整打通授权闭环。

## 协议示例

请求：

```json
{ "filePath": "D:\\docs\\sample.docx" }
```

响应：

```json
{
  "filePath": "D:\\docs\\sample.docx",
  "fileType": "docx",
  "parser": "dotnet-sidecar-v1",
  "pageCount": null,
  "warnings": ["..."]
}
```
