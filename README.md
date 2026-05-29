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

1. 安装 Node.js / Rust / .NET 8 SDK
2. 建议安装 `.NET Framework 4.8 Developer Pack`（用于构建 net48 sidecar）
2. 在项目根目录执行：

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
  /t:Build /p:Configuration=Debug /v:minimal
```

构建后可执行文件：

`sidecar/DocParserSidecarNet48/bin/Debug/DocParserSidecarNet48.exe`

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
