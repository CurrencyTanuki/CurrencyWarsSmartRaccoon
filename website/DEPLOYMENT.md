# 网站本地运行与云服务器部署

## 本地预览

1. 安装 Node.js 22.13 或更高版本。
2. 在 `website` 目录安装依赖并运行开发服务。
3. 浏览终端显示的本地地址。

```powershell
pnpm install
pnpm dev
```

正式构建：

```powershell
$env:NEXT_PUBLIC_SITE_URL = "https://你的域名"
pnpm build
```

## 发布前必须填写

- `app/page.tsx` 中的最新版本下载地址；
- QQ 交流群；
- 源码仓库与正式许可证链接；
- 问题提交地址；
- `NEXT_PUBLIC_SITE_URL` 的正式域名。

## 云服务器建议

当前网站为静态内容，不需要数据库。可将构建产物部署到支持静态站点或 Cloudflare Worker 的服务。配置 HTTPS、域名和缓存后，再把正式地址同步到桌面程序的 `config/community.json`。

本仓库没有服务器地址、账号、域名或部署授权，因此当前只完成本地构建，不执行外部部署。
