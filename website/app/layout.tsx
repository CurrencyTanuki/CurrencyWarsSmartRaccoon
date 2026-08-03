import type { Metadata } from "next";
import "./globals.css";

const siteUrl = process.env.NEXT_PUBLIC_SITE_URL ?? "http://localhost:3000";

export const metadata: Metadata = {
  metadataBase: new URL(siteUrl),
  title: "货币战争小助手｜免费桌面工具",
  description:
    "自动重刷开局、对局状态记录、节点历史与挑战总结。只通过画面识别和 Windows 输入接口工作。",
  icons: {
    icon: "/app-icon.png",
    shortcut: "/app-icon.png",
  },
  openGraph: {
    title: "货币战争小助手",
    description: "自动重刷开局 · 对局记录 · 挑战总结",
    type: "website",
    images: [{ url: "/og.png", width: 1200, height: 630 }],
  },
};

export default function RootLayout({
  children,
}: Readonly<{
  children: React.ReactNode;
}>) {
  return (
    <html lang="zh-CN">
      <body>{children}</body>
    </html>
  );
}
