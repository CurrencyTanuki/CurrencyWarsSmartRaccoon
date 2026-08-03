export default function Home() {
  return (
    <main>
      <header className="site-header">
        <a className="brand" href="#top" aria-label="返回首页">
          <img src="/app-icon.png" alt="" width="38" height="38" />
          <span>货币战争小助手</span>
        </a>
        <nav aria-label="主导航">
          <a href="#features">功能</a>
          <a href="#guide">使用</a>
          <a href="#safety">安全边界</a>
          <a href="#download">下载</a>
        </nav>
      </header>

      <section className="hero" id="top">
        <div className="hero-copy">
          <p className="eyebrow">免费 · 源代码公开 · Windows 10 / 11</p>
          <h1>把开局重刷和对局复盘，交给可靠的画面识别。</h1>
          <p className="lede">
            自动筛选理想开局，持续记录节点伤害、行动值、经济与阵容变化，
            对局结束后生成可追溯的挑战总结。
          </p>
          <div className="hero-actions">
            <a className="button primary" href="#download">获取最新版本</a>
            <a className="button quiet" href="#guide">查看使用方法</a>
          </div>
          <p className="refund-note">
            如果你曾为本软件付费，请向商家申请退款。官方版本不收取软件购买费。
          </p>
        </div>
        <div className="hero-visual" aria-label="软件能力概览">
          <div className="orbit orbit-one" />
          <div className="orbit orbit-two" />
          <img src="/app-icon.png" alt="货币战争小助手图标" width="184" height="184" />
          <div className="metric metric-a"><strong>4–6 FPS</strong><span>轻量场景检测</span></div>
          <div className="metric metric-b"><strong>节点级</strong><span>最终状态留存</span></div>
          <div className="metric metric-c"><strong>可追溯</strong><span>截图与置信度</span></div>
        </div>
      </section>

      <section className="section" id="features">
        <div className="section-heading">
          <p className="eyebrow">核心能力</p>
          <h2>从一次开局，到整局复盘。</h2>
        </div>
        <div className="feature-grid">
          <article><span className="feature-index">01</span><h3>自动重刷开局</h3><p>按投资环境、敌人阵营、负面词条、投资策略和特殊组合筛选，并对每次输入进行结果验证。</p></article>
          <article><span className="feature-index">02</span><h3>实时对局记录</h3><p>高频检测页面变化，后台识别值得保留的候选帧，只保存节点最终有效状态。</p></article>
          <article><span className="feature-index">03</span><h3>节点历史看板</h3><p>紧凑展示最终伤害、剩余行动值、金币变化、理论出伤与通关状态。</p></article>
          <article><span className="feature-index">04</span><h3>挑战总结</h3><p>整局结束后自动封存数据并生成离线报告；缺失字段不会被伪造为零。</p></article>
          <article><span className="feature-index">05</span><h3>断点续录</h3><p>程序或游戏中断后保留已采集内容，可从原记录继续，跳过的节点保持空白。</p></article>
          <article><span className="feature-index">06</span><h3>字段级降级</h3><p>遮挡或未知画面只影响不可见字段，可见区域继续识别，并保留失败原因和原始证据。</p></article>
        </div>
      </section>

      <section className="section split" id="guide">
        <div className="section-heading sticky-heading">
          <p className="eyebrow">开始使用</p>
          <h2>三步完成首次配置。</h2>
          <p>无需管理员权限，不读取游戏内存，也不修改游戏文件。</p>
        </div>
        <ol className="steps">
          <li><span>1</span><div><h3>启动游戏与助手</h3><p>在助手中选择游戏窗口，先截取测试画面确认捕获正常。</p></div></li>
          <li><span>2</span><div><h3>选择使用方式</h3><p>配置理想开局后自动刷取，或直接打开实时分析开始记录。</p></div></li>
          <li><span>3</span><div><h3>正常游玩</h3><p>软件在后台更新记录；节点历史看板和对局报告会使用同一套数据。</p></div></li>
        </ol>
      </section>

      <section className="section safety" id="safety">
        <div>
          <p className="eyebrow">安全边界</p>
          <h2>只看画面，只发送普通输入。</h2>
        </div>
        <ul>
          <li>不读取或修改游戏内存</li>
          <li>不注入游戏进程</li>
          <li>不修改游戏文件</li>
          <li>不确定时停止，不猜测点击</li>
          <li>每个关键操作都有验证与重试上限</li>
          <li>Ctrl + Shift + F12 可随时紧急停止</li>
        </ul>
        <p className="risk-note">第三方自动化工具可能受游戏规则约束；请在使用前自行了解并承担相关风险。</p>
      </section>

      <section className="section download" id="download">
        <div>
          <p className="eyebrow">下载与反馈</p>
          <h2>发布信息即将补充。</h2>
          <p>正式下载地址、源码仓库、问题提交入口和 QQ 交流群将在发布配置确认后统一更新。</p>
        </div>
        <div className="download-actions">
          <span className="button disabled" aria-disabled="true">最新版本 · 待补充</span>
          <span className="meta">QQ 交流群：待补充</span>
          <span className="meta">源码与许可证：待补充</span>
          <span className="meta">问题提交：待补充</span>
        </div>
      </section>

      <footer>
        <div className="brand compact"><img src="/app-icon.png" alt="" width="30" height="30" /><span>货币战争小助手</span></div>
        <p>非官方社区工具，与游戏开发商及发行商无关联。</p>
      </footer>
    </main>
  );
}
