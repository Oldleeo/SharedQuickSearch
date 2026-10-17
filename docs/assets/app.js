/* 共享速搜 官网脚本
   - 首屏的搜索演示：和软件同样的规则（SKU 容错、拼音首字母、条件胶囊、空格预览、滚轮缩放）
   - 从 GitHub Releases 读取最新版本号、大小和下载地址
   不依赖任何第三方库。 */
(() => {
  "use strict";

  // 仓库地址：改成你自己的仓库只需要改这一行（页面里的链接会跟着改）
  const REPO = "oldleeo/SharedQuickSearch";
  const ASSET = /setup.*\.exe$/i;

  const reduceMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
  const $ = (s, root = document) => root.querySelector(s);
  const $$ = (s, root = document) => [...root.querySelectorAll(s)];

  // ================= 仓库链接 + 最新版本 =================

  const repoUrl = `https://github.com/${REPO}`;
  const fallbackDownload = `${repoUrl}/releases/latest/download/SharedQuickSearch-Setup.exe`;
  $$("[data-repo-link]").forEach(a => (a.href = repoUrl));
  $$("[data-releases-link]").forEach(a => (a.href = `${repoUrl}/releases`));
  $$("[data-issues-link]").forEach(a => (a.href = `${repoUrl}/issues`));
  $$("[data-privacy-link]").forEach(a => (a.href = `${repoUrl}/blob/main/PRIVACY.md`));
  $$("[data-download]").forEach(a => (a.href = fallbackDownload));

  fetch(`https://api.github.com/repos/${REPO}/releases/latest`, { headers: { Accept: "application/vnd.github+json" } })
    .then(r => (r.ok ? r.json() : null))
    .then(release => {
      if (!release || !Array.isArray(release.assets)) return;
      const asset = release.assets.find(a => ASSET.test(a.name)) || release.assets.find(a => /\.exe$/i.test(a.name));
      const version = String(release.tag_name || "").replace(/^v/i, "");
      if (version) $$("[data-version]").forEach(el => (el.textContent = `版本 ${version}`));
      if (asset) {
        $$("[data-download]").forEach(a => (a.href = asset.browser_download_url));
        $$("[data-size]").forEach(el => (el.textContent = `${Math.round(asset.size / 1048576)} MB`));
      }
      if (release.published_at) {
        const d = new Date(release.published_at);
        $$("[data-date]").forEach(el => (el.textContent = `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")} 发布`));
      }
    })
    .catch(() => { /* 访问不了 GitHub API 时保留默认链接（总是指向最新版） */ });

  // 顶栏滚动后出现分隔线
  const top = $("#top");
  const onScroll = () => top.classList.toggle("scrolled", window.scrollY > 8);
  window.addEventListener("scroll", onScroll, { passive: true });
  onScroll();

  // ================= 示例缩略图（SVG 画的“商品图”） =================

  const PALETTE = {
    blue: ["#9FB6F7", "#2457E6", "#16357F"],
    amber: ["#FFD99A", "#E39A1C", "#8A5506"],
    violet: ["#CDBDFB", "#7B5CF0", "#3E2A8F"],
    green: ["#A8E6C2", "#22A55B", "#0F5A31"],
    rose: ["#FBC4CF", "#E0567A", "#7D1E38"]
  };

  function product(kind, c, x = 100, y = 100, s = 1) {
    const g = `<g transform="translate(${x - 100 * s} ${y - 100 * s}) scale(${s})">`;
    switch (kind) {
      case "box":
        return `${g}<path d="M58 78 100 60l42 18v70l-42 18-42-18z" fill="${c[1]}"/><path d="M58 78 100 96l42-18-42-18z" fill="${c[0]}"/><path d="M100 96v70l42-18V78z" fill="${c[2]}" opacity=".55"/><rect x="68" y="108" width="22" height="26" rx="3" fill="#fff" opacity=".85" transform="skewY(22) translate(0 -36)"/></g>`;
      case "jar":
        return `${g}<rect x="60" y="82" width="80" height="78" rx="24" fill="${c[1]}"/><rect x="64" y="64" width="72" height="24" rx="9" fill="${c[2]}"/><rect x="74" y="104" width="52" height="30" rx="6" fill="#fff" opacity=".85"/><rect x="70" y="90" width="10" height="56" rx="5" fill="#fff" opacity=".22"/></g>`;
      case "tube":
        return `${g}<path d="M82 40h36l8 108H74z" fill="${c[1]}"/><rect x="86" y="148" width="28" height="18" rx="4" fill="${c[2]}"/><rect x="84" y="36" width="32" height="8" rx="2" fill="${c[2]}"/><rect x="88" y="78" width="24" height="40" rx="4" fill="#fff" opacity=".85"/></g>`;
      default:
        return `${g}<rect x="76" y="56" width="48" height="108" rx="19" fill="${c[1]}"/><rect x="88" y="36" width="24" height="24" rx="5" fill="${c[2]}"/><rect x="80" y="98" width="40" height="36" rx="5" fill="#fff" opacity=".85"/><rect x="83" y="66" width="8" height="84" rx="4" fill="#fff" opacity=".25"/></g>`;
    }
  }

  const bars = (x, y, widths, color = "#14161B", op = .85) =>
    widths.map((w, i) => `<rect x="${x}" y="${y + i * 12}" width="${w}" height="${i === 0 ? 8 : 5}" rx="2.5" fill="${color}" opacity="${i === 0 ? op : op * .45}"/>`).join("");

  function art(item, w = 200, h = 200) {
    const c = PALETTE[item.hue] || PALETTE.blue;
    let body = "";
    switch (item.kind) {
      case "white":
        body = `<rect width="200" height="200" fill="#fff"/><ellipse cx="100" cy="168" rx="46" ry="7" fill="#000" opacity=".08"/>${product(item.prod, c)}`;
        break;
      case "scene":
        body = `<defs><linearGradient id="s" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="${c[0]}" stop-opacity=".55"/><stop offset="1" stop-color="#F6EFE6"/></linearGradient></defs>
          <rect width="200" height="200" fill="url(#s)"/><rect y="148" width="200" height="52" fill="#E8DCCB"/><circle cx="160" cy="70" r="22" fill="${c[1]}" opacity=".18"/>
          <rect x="26" y="96" width="14" height="56" rx="3" fill="#7D9B6C"/><circle cx="33" cy="88" r="16" fill="#8DB27A"/>${product(item.prod, c, 108, 102, .9)}`;
        break;
      case "main":
        body = `<defs><linearGradient id="m" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="${c[0]}"/><stop offset="1" stop-color="#fff"/></linearGradient></defs>
          <rect width="200" height="200" fill="url(#m)"/>${bars(16, 18, [88, 64, 52])}<circle cx="166" cy="34" r="18" fill="${c[1]}"/><text x="166" y="38" font-size="11" font-family="sans-serif" font-weight="700" fill="#fff" text-anchor="middle">新品</text>
          ${product(item.prod, c, 112, 118, .82)}<rect x="16" y="168" width="56" height="16" rx="8" fill="${c[2]}"/>`;
        break;
      case "detail":
        body = `<rect width="200" height="200" fill="#fff"/><rect width="200" height="70" fill="${c[0]}" opacity=".6"/>${bars(14, 14, [70, 50])}${product(item.prod, c, 150, 44, .42)}
          <rect x="14" y="84" width="80" height="50" rx="6" fill="${c[1]}" opacity=".12"/>${product(item.prod, c, 54, 110, .26)}${bars(104, 90, [80, 72, 60, 70], "#14161B", .6)}
          <rect x="14" y="146" width="172" height="40" rx="6" fill="#F3F4F7"/>${bars(24, 156, [60, 120, 100], "#14161B", .5)}`;
        break;
      case "dieline":
        body = `<rect width="200" height="200" fill="#fff"/><g fill="none" stroke-width="1.3">
          <path d="M70 20h60v40H70zM30 60h40v80H30zM70 60h60v80H70zM130 60h40v80h-40zM70 140h60v40H70z" stroke="#2457E6"/>
          <path d="M70 60v80M130 60v80M70 60h60M70 140h60" stroke="#E0567A" stroke-dasharray="4 3"/></g>
          ${product(item.prod, c, 100, 100, .34)}<rect x="78" y="150" width="44" height="6" rx="3" fill="${c[1]}" opacity=".6"/>`;
        break;
      case "banner":
        body = `<defs><linearGradient id="b" x1="0" y1="0" x2="1" y2="0"><stop offset="0" stop-color="${c[1]}"/><stop offset="1" stop-color="${c[0]}"/></linearGradient></defs>
          <rect width="200" height="200" fill="#fff"/><rect y="60" width="200" height="80" fill="url(#b)"/>${bars(14, 78, [80, 60, 40], "#fff", .95)}
          <rect x="14" y="118" width="40" height="12" rx="6" fill="#fff"/>${product(item.prod, c, 150, 100, .5)}${product("jar", PALETTE.rose, 176, 112, .32)}`;
        break;
      case "poster":
        body = `<rect width="200" height="200" fill="#F3EFE9"/><rect x="40" y="14" width="120" height="172" rx="4" fill="${c[1]}"/>
          <circle cx="100" cy="82" r="38" fill="${c[0]}" opacity=".7"/>${product(item.prod, c, 100, 92, .5)}${bars(54, 140, [92, 70, 50], "#fff", .95)}`;
        break;
      case "page":
        body = `<rect width="200" height="200" fill="#F5F6F8"/><rect x="34" y="18" width="132" height="166" rx="5" fill="#fff" stroke="#E4E6EB"/>
          ${bars(48, 34, [70, 100, 96, 104, 88, 100, 60], "#14161B", .6)}<rect x="48" y="128" width="104" height="40" rx="4" fill="${c[0]}" opacity=".6"/>`;
        break;
      case "swatch":
        body = `<rect width="200" height="200" fill="#fff"/>${["#2457E6", "#7B5CF0", "#E39A1C", "#22A55B", "#E0567A", "#14161B", "#9FB6F7", "#FFD99A", "#A8E6C2"]
          .map((col, i) => `<rect x="${24 + (i % 3) * 52}" y="${24 + Math.floor(i / 3) * 52}" width="44" height="44" rx="8" fill="${col}"/>`).join("")}`;
        break;
      case "video":
        body = `<rect width="200" height="200" fill="#1A1C22"/><rect x="0" y="34" width="200" height="132" fill="${c[2]}" opacity=".55"/>${product(item.prod, c, 100, 100, .7)}
          <circle cx="100" cy="100" r="22" fill="#000" opacity=".45"/><path d="M93 89v22l19-11z" fill="#fff"/><rect x="12" y="176" width="176" height="3" rx="1.5" fill="#fff" opacity=".25"/><rect x="12" y="176" width="70" height="3" rx="1.5" fill="#fff"/>`;
        break;
    }
    return `data:image/svg+xml;charset=utf-8,${encodeURIComponent(`<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 200 200" width="${w}" height="${h}">${body}</svg>`)}`;
  }

  // ================= 示例文件 =================

  const D = "\\\\NAS\\设计部\\2026\\";
  const raw = [
    ["SKU_14_5 白底图.jpg", "white", "bottle", "blue", 2.4, 1, 3000, 3000, "春季\\SKU_14_5"],
    ["14-5 主图 v3.psd", "main", "bottle", "blue", 186, 0, 800, 800, "春季\\SKU_14_5"],
    ["14-5 详情页 长图.psd", "detail", "bottle", "blue", 412, 2, 790, 12000, "春季\\SKU_14_5"],
    ["SKU145 场景图 客厅.jpg", "scene", "bottle", "blue", 6.8, 3, 4000, 4000, "春季\\SKU_14_5"],
    ["14-5 包装展开图.ai", "dieline", "box", "blue", 24, 9, 0, 0, "春季\\SKU_14_5"],
    ["14-5 产品视频 15s.mp4", "video", "bottle", "blue", 96, 4, 1920, 1080, "春季\\SKU_14_5"],
    ["SKU_14_5 白底图 侧面.png", "white", "bottle", "blue", 3.1, 1, 3000, 3000, "春季\\SKU_14_5"],
    ["14_5 主图 旧版.psd", "main", "bottle", "rose", 160, 40, 800, 800, "春季\\SKU_14_5\\旧版"],
    ["SKU_14_5 素材", "folder", "", "", 0, 0, 0, 0, "春季"],
    ["14-5 主图 v2.psd", "main", "bottle", "violet", 172, 12, 800, 800, "春季\\SKU_14_5"],
    ["BX-2207 白底图.jpg", "white", "box", "amber", 2.1, 5, 3000, 3000, "夏季\\BX-2207"],
    ["BX-2207 主图.psd", "main", "box", "amber", 148, 5, 800, 800, "夏季\\BX-2207"],
    ["BX-2207 场景图 户外.jpg", "scene", "box", "amber", 7.4, 6, 4000, 3000, "夏季\\BX-2207"],
    ["BX2207 包装展开图.ai", "dieline", "box", "amber", 31, 20, 0, 0, "夏季\\BX-2207"],
    ["BX-2207 白底图 背面.jpg", "white", "box", "amber", 2.0, 5, 3000, 3000, "夏季\\BX-2207"],
    ["MS-0318 白底图.jpg", "white", "jar", "violet", 1.8, 2, 2400, 2400, "春季\\MS-0318"],
    ["MS-0318 主图 v2.psd", "main", "jar", "violet", 133, 2, 800, 800, "春季\\MS-0318"],
    ["MS-0318 主图 旧版.psd", "main", "jar", "amber", 120, 33, 800, 800, "春季\\MS-0318\\旧版"],
    ["MS-0318 详情页.psd", "detail", "jar", "violet", 356, 7, 790, 9800, "春季\\MS-0318"],
    ["MS-0318 场景图.jpg", "scene", "jar", "violet", 5.9, 8, 4000, 4000, "春季\\MS-0318"],
    ["MS-0318 白底图 组合.jpg", "white", "jar", "rose", 2.6, 2, 3000, 2000, "春季\\MS-0318"],
    ["22-09 白底图.png", "white", "tube", "green", 1.6, 15, 2400, 2400, "秋季\\22-09"],
    ["22-09 主图.psd", "main", "tube", "green", 98, 15, 800, 800, "秋季\\22-09"],
    ["22-09 产品视频.mp4", "video", "tube", "green", 120, 18, 1920, 1080, "秋季\\22-09"],
    ["22-09 场景图 浴室.jpg", "scene", "tube", "green", 6.1, 16, 4000, 4000, "秋季\\22-09"],
    ["春季上新 banner 1920x600.psd", "banner", "bottle", "blue", 88, 1, 1920, 600, "活动\\春季上新"],
    ["春季上新 海报.psd", "poster", "jar", "violet", 210, 3, 3508, 4961, "活动\\春季上新"],
    ["店铺首页 设计稿.psd", "banner", "box", "amber", 264, 10, 1920, 5200, "店铺"],
    ["白底图 规范说明.pdf", "page", "", "blue", 0.8, 60, 0, 0, "规范"],
    ["品牌色卡 2026.ai", "swatch", "", "blue", 4.2, 90, 0, 0, "规范"],
    ["白底图合集 2026", "folder", "", "", 0, 30, 0, 0, ""]
  ];

  const now = new Date();
  const ITEMS = raw.map(([name, kind, prod, hue, mb, days, w, h, dir], index) => {
    const folder = kind === "folder";
    const ext = folder ? "" : name.slice(name.lastIndexOf(".") + 1).toLowerCase();
    const item = { index, name, kind, prod, hue, mb, days, w, h, folder, ext, dir: D + dir };
    item.modified = new Date(now.getFullYear(), now.getMonth(), now.getDate() - days, 9 + (index * 7) % 9, (index * 13) % 60);
    item.thumb = folder ? null : art(item);
    return item;
  });

  // 只收录示例文件名里用到的字：拼音全拼（取常用读音，和软件一样）
  const PY = {
    白: "bai", 底: "di", 图: "tu", 主: "zhu", 详: "xiang", 情: "qing", 页: "ye", 长: "zhang", 场: "chang", 景: "jing", 客: "ke", 厅: "ting",
    包: "bao", 装: "zhuang", 展: "zhan", 开: "kai", 产: "chan", 品: "pin", 视: "shi", 频: "pin", 侧: "ce", 面: "mian", 旧: "jiu", 版: "ban",
    素: "su", 材: "cai", 户: "hu", 外: "wai", 背: "bei", 组: "zu", 合: "he", 浴: "yu", 室: "shi", 春: "chun", 季: "ji", 上: "shang", 新: "xin",
    海: "hai", 报: "bao", 店: "dian", 铺: "pu", 首: "shou", 设: "she", 计: "ji", 稿: "gao", 规: "gui", 范: "fan", 说: "shuo", 明: "ming",
    牌: "pai", 色: "se", 卡: "ka", 集: "ji"
  };
  const compact = s => s.toLowerCase().replace(/[\s\-_.　－]/g, "");
  ITEMS.forEach(item => {
    const base = item.folder ? item.name : item.name.slice(0, item.name.lastIndexOf("."));
    let full = "", initials = "";
    for (const ch of base) {
      if (PY[ch]) { full += PY[ch]; initials += PY[ch][0]; }
      else if (/[a-z0-9]/i.test(ch)) { full += ch.toLowerCase(); initials += ch.toLowerCase(); }
    }
    item.c = compact(item.name);
    item.full = full;
    item.initials = initials;
  });

  const TYPE = {
    jpg: ["JPG", "#E39A1C", "图片"], png: ["PNG", "#E39A1C", "图片"], psd: ["PSD", "#2457E6", "设计稿"], ai: ["AI", "#E07B1C", "设计稿"],
    mp4: ["MP4", "#22A55B", "视频"], pdf: ["PDF", "#D93A3A", "文档"]
  };
  const CATS = [
    ["all", "全部"], ["image", "图片"], ["design", "PSD · AI"], ["doc", "文档"], ["video", "视频"], ["folder", "文件夹"]
  ];
  const catOf = item => item.folder ? "folder" : { jpg: "image", png: "image", psd: "design", ai: "design", pdf: "doc", mp4: "video" }[item.ext];
  const sizeLabel = mb => mb >= 1000 ? `${(mb / 1024).toFixed(1)} GB` : mb >= 10 ? `${Math.round(mb)} MB` : mb >= 1 ? `${mb.toFixed(1)} MB` : `${Math.round(mb * 1024)} KB`;
  const dateLabel = d => `${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;

  // ================= 搜索语法（和软件一致的子集） =================

  const EXTS = new Set(["psd", "jpg", "jpeg", "png", "ai", "mp4", "pdf"]);
  const CAT_WORDS = { 图片: "image", 照片: "image", 设计稿: "design", 源文件: "design", 文档: "doc", 视频: "video", 文件夹: "folder" };

  function parse(text) {
    const q = { terms: [], exts: [], cat: null, min: null, max: null, since: null, excludes: [], tokens: [] };
    for (const part of text.split(/[\s　]+/).filter(Boolean)) {
      const lower = part.toLowerCase().replace(/^\*?\./, "");
      let m;
      if (EXTS.has(lower)) { q.exts.push(lower === "jpeg" ? "jpg" : lower); q.tokens.push([part, lower.toUpperCase()]); }
      else if (CAT_WORDS[part]) { q.cat = CAT_WORDS[part]; }
      else if ((m = /^([<>])(\d+(?:\.\d+)?)(k|kb|m|mb|g|gb)?$/i.exec(part))) {
        const unit = (m[3] || "mb").toLowerCase();
        const mb = parseFloat(m[2]) * (unit[0] === "k" ? 1 / 1024 : unit[0] === "g" ? 1024 : 1);
        if (m[1] === ">") q.min = mb; else q.max = mb;
        q.tokens.push([part, `${m[1] === ">" ? "大于" : "小于"} ${m[2]}${(m[3] || "MB").toUpperCase()}`]);
      }
      else if (part === "今天" || part === "昨天" || part === "本周" || part === "本月" || (m = /^(\d{1,3})(?:d|天)$/i.exec(part))) {
        const today = new Date(now.getFullYear(), now.getMonth(), now.getDate());
        let since = today, label = part;
        if (part === "昨天") { since = new Date(today - 864e5); label = "昨天以来"; }
        else if (part === "本周") { since = new Date(today - ((today.getDay() + 6) % 7) * 864e5); }
        else if (part === "本月") { since = new Date(today.getFullYear(), today.getMonth(), 1); }
        else if (m) { since = new Date(today - (parseInt(m[1], 10) - 1) * 864e5); label = `近 ${m[1]} 天`; }
        q.since = since;
        q.tokens.push([part, label]);
      }
      else if (part.length > 1 && part[0] === "-" && !/^-\d/.test(part)) { q.excludes.push(part.slice(1)); q.tokens.push([part, `排除 ${part.slice(1)}`]); }
      else q.terms.push(part);
    }
    return q;
  }

  function matchTerm(item, term) {
    const t = term.toLowerCase();
    if (item.name.toLowerCase().includes(t)) return true;
    const ct = compact(t);
    if (!ct) return true;
    return item.c.includes(ct) || (/^[a-z0-9]+$/.test(ct) && (item.full.includes(ct) || item.initials.includes(ct)));
  }

  function search(text, cat) {
    const q = parse(text);
    const effectiveCat = q.cat || cat;
    const base = ITEMS.filter(item => {
      if (q.exts.length && !q.exts.includes(item.ext)) return false;
      if (q.min !== null && item.mb <= q.min) return false;
      if (q.max !== null && (item.folder || item.mb >= q.max)) return false;
      if (q.since && item.modified < q.since) return false;
      if (q.excludes.some(x => item.name.toLowerCase().includes(x.toLowerCase()))) return false;
      return q.terms.every(t => matchTerm(item, t));
    });
    const counts = { all: base.length };
    base.forEach(item => (counts[catOf(item)] = (counts[catOf(item)] || 0) + 1));
    const key = compact(q.terms.join(""));
    const items = base
      .filter(item => effectiveCat === "all" || catOf(item) === effectiveCat)
      .sort((a, b) => {
        const sa = a.c.startsWith(key) || a.c.includes("sku" + key) ? 0 : 1;
        const sb = b.c.startsWith(key) || b.c.includes("sku" + key) ? 0 : 1;
        return sa - sb || a.days - b.days || a.index - b.index;
      });
    return { q, items, counts, cat: effectiveCat };
  }

  const esc = s => s.replace(/[&<>"]/g, ch => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[ch]));

  function highlight(name, terms) {
    const lower = name.toLowerCase();
    for (const term of [...terms].sort((a, b) => b.length - a.length)) {
      const at = lower.indexOf(term.toLowerCase());
      if (at >= 0) return esc(name.slice(0, at)) + "<mark>" + esc(name.slice(at, at + term.length)) + "</mark>" + esc(name.slice(at + term.length));
    }
    return esc(name);
  }

  // ================= 首屏演示 =================

  const spot = $("#spot");
  const input = $("#spot-input");
  const grid = $("#spot-grid");
  const cats = $("#spot-cats");
  const tokens = $("#spot-tokens");
  const stats = $("#spot-stats");
  const empty = $("#spot-empty");
  const foot = $(".spot-foot");
  const footToast = $("#foot-toast");

  let category = "all";
  let results = [];
  let current = -1;
  let gridMode = false;
  let showingRecent = false;
  let userTouched = false;

  const CHECK = `<svg viewBox="0 0 10 10"><path d="m2 5.2 2 2 4-4.4" fill="none" stroke="#fff" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"/></svg>`;
  const FOLDER = `<svg class="tile-folder" viewBox="0 0 48 40"><path d="M3 9a4 4 0 0 1 4-4h11l5 5h18a4 4 0 0 1 4 4v19a4 4 0 0 1-4 4H7a4 4 0 0 1-4-4z" fill="#F2B544"/><path d="M3 15h42v17a4 4 0 0 1-4 4H7a4 4 0 0 1-4-4z" fill="#F7C95E"/></svg>`;

  function renderCats(counts, active) {
    cats.innerHTML = CATS.map(([key, label]) => {
      const n = counts[key] || 0;
      return `<button type="button" class="chip${n ? "" : " dim"}" role="tab" aria-selected="${key === active}" data-cat="${key}">${label}${n ? `<small>${n}</small>` : ""}</button>`;
    }).join("");
  }

  function renderTokens(list) {
    tokens.innerHTML = list.map(([raw, label]) =>
      `<button type="button" class="token" data-raw="${esc(raw)}" title="点击移除这个条件">${esc(label)}<svg viewBox="0 0 8 8"><path d="m1 1 6 6m0-6-6 6" stroke="currentColor" stroke-width="1.4" stroke-linecap="round"/></svg></button>`).join("");
  }

  function tileHtml(item, i, terms) {
    const type = TYPE[item.ext];
    const meta = item.folder ? `文件夹 · ${dateLabel(item.modified)}` : `${sizeLabel(item.mb)} · ${dateLabel(item.modified)}`;
    const delay = reduceMotion ? 0 : Math.min(i, 24) * 14;
    return `<div class="tile" role="option" aria-selected="false" data-i="${i}" draggable="true" style="animation-delay:${delay}ms">
      <div class="tile-thumb">${item.folder ? FOLDER : `<img src="${item.thumb}" alt="" loading="lazy" draggable="false">`}
        ${type ? `<span class="badge" style="background:${type[1]}">${type[0]}</span>` : ""}
        <span class="tile-check">${CHECK}</span><span class="tile-flash">${CHECK}</span></div>
      <div class="tile-name">${highlight(item.name, terms)}</div>
      <div class="tile-meta">${meta}</div></div>`;
  }

  function setState(state) { spot.dataset.state = state; }

  function run() {
    const text = input.value;
    showingRecent = false;
    if (!text.trim()) { collapse(); return; }
    const res = search(text, category);
    results = res.items;
    renderCats(res.counts, res.cat);
    renderTokens(res.q.tokens);
    grid.innerHTML = results.map((item, i) => tileHtml(item, i, res.q.terms)).join("");
    grid.scrollTop = 0;
    empty.hidden = results.length > 0;
    if (!results.length) $("#spot-empty-title").textContent = `没有找到“${text.trim()}”`;
    setState(results.length ? "open" : "empty");
    rollCount(res.counts.all);
    select(results.length ? 0 : -1, false);
  }

  function showRecent() {
    showingRecent = true;
    results = [...ITEMS].sort((a, b) => a.days - b.days || a.index - b.index).slice(0, 20);
    renderCats({ all: results.length }, "all");
    tokens.innerHTML = `<span class="chip" style="cursor:default">最近使用</span>`;
    grid.innerHTML = results.map((item, i) => tileHtml(item, i, [])).join("");
    empty.hidden = true;
    stats.textContent = "";
    setState("open");
    gridMode = true;
    select(0);
  }

  function collapse() {
    results = [];
    current = -1;
    gridMode = false;
    stats.textContent = "";
    setState("collapsed");
    updateFoot();
  }

  // 结果数从旧值滚到新值
  let shownCount = 0, countRaf = 0;
  function rollCount(target) {
    cancelAnimationFrame(countRaf);
    const from = shownCount, start = performance.now(), dur = reduceMotion ? 0 : 260;
    const step = t => {
      const p = dur ? Math.min(1, (t - start) / dur) : 1;
      shownCount = Math.round(from + (target - from) * (1 - Math.pow(1 - p, 3)));
      stats.textContent = `${shownCount} 项`;
      if (p < 1) countRaf = requestAnimationFrame(step);
    };
    countRaf = requestAnimationFrame(step);
  }

  function columns() {
    return getComputedStyle(grid).gridTemplateColumns.split(" ").filter(Boolean).length || 5;
  }

  function select(i, scroll = true) {
    $$(".tile[aria-selected='true']", grid).forEach(t => t.setAttribute("aria-selected", "false"));
    current = i;
    const tile = grid.querySelector(`.tile[data-i="${i}"]`);
    if (tile) {
      tile.setAttribute("aria-selected", "true");
      if (scroll) {
        const top = tile.offsetTop - 6, bottom = top + tile.offsetHeight + 12;
        if (top < grid.scrollTop) grid.scrollTo({ top, behavior: reduceMotion ? "auto" : "smooth" });
        else if (bottom > grid.scrollTop + grid.clientHeight - 24) grid.scrollTo({ top: bottom - grid.clientHeight + 30, behavior: reduceMotion ? "auto" : "smooth" });
      }
    }
    updateFoot();
  }

  function updateFoot() {
    const item = results[current];
    const thumb = $("#foot-thumb");
    if (!item) {
      thumb.style.backgroundImage = "";
      $("#foot-name").textContent = "输入 SKU、文件名或拼音首字母";
      $("#foot-path").textContent = "按 ↓ 查看最近使用的文件";
      return;
    }
    thumb.style.backgroundImage = item.thumb ? `url("${item.thumb}")` : "";
    $("#foot-name").textContent = item.w ? `${item.name}  ·  ${item.w} × ${item.h}  ·  ${sizeLabel(item.mb)}` : item.folder ? item.name : `${item.name}  ·  ${sizeLabel(item.mb)}`;
    $("#foot-path").textContent = item.dir;
  }

  let toastTimer = 0;
  function footSay(text) {
    footToast.innerHTML = `<svg viewBox="0 0 10 10" width="12" height="12"><path d="m2 5.2 2 2 4-4.4" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"/></svg>${esc(text)}`;
    foot.classList.add("toasting");
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => foot.classList.remove("toasting"), 2400);
  }

  function flash(i) {
    const tile = grid.querySelector(`.tile[data-i="${i}"]`);
    if (!tile) return;
    tile.classList.remove("flash");
    void tile.offsetWidth;
    tile.classList.add("flash");
  }

  let searchTimer = 0;
  input.addEventListener("input", () => {
    userTouched = true;
    stopAutoType();
    gridMode = false;
    clearTimeout(searchTimer);
    searchTimer = setTimeout(run, 60);
  });

  input.addEventListener("keydown", e => {
    if (lookOpen) return;
    const cols = columns();
    switch (e.key) {
      case "ArrowDown":
        e.preventDefault();
        if (!results.length) { if (!input.value.trim()) showRecent(); }
        else if (!gridMode) { gridMode = true; select(Math.max(0, current)); }
        else select(Math.min(results.length - 1, current + cols));
        break;
      case "ArrowUp":
        if (!gridMode) return;
        e.preventDefault();
        if (current < cols) gridMode = false; else select(current - cols);
        break;
      case "ArrowLeft": case "ArrowRight":
        if (!gridMode || !results.length) return;
        e.preventDefault();
        select(Math.max(0, Math.min(results.length - 1, current + (e.key === "ArrowLeft" ? -1 : 1))));
        break;
      case " ":
        if (!gridMode || !results.length) return;
        e.preventDefault();
        e.stopPropagation(); // 同一次按键不要再冒泡到“预览中按空格关闭”
        openLook(current);
        break;
      case "Enter":
        e.preventDefault();
        if (!results[current]) return;
        if (e.ctrlKey && e.shiftKey) footSay("Ctrl+Shift+Enter：展开成工作台");
        else if (e.ctrlKey) footSay(`打开 ${results[current].name}（演示里不会真的打开）`);
        else footSay(`打开所在位置：${results[current].dir}`);
        break;
      case "Escape":
        e.preventDefault();
        if (input.value) { input.value = ""; collapse(); } else input.blur();
        break;
      case "c": case "C":
        if (!(e.ctrlKey || e.metaKey) || !results[current] || input.selectionStart !== input.selectionEnd) return;
        e.preventDefault();
        if (e.shiftKey) { flash(current); footSay("文件已复制，可以直接粘贴（演示）"); }
        else footSay("完整路径已复制（演示）");
        break;
      case "Backspace": case "Delete":
        gridMode = false;
        break;
    }
  });

  cats.addEventListener("click", e => {
    const chip = e.target.closest("[data-cat]");
    if (!chip || showingRecent) return;
    category = chip.dataset.cat;
    run();
    input.focus({ preventScroll: true });
  });

  tokens.addEventListener("click", e => {
    const token = e.target.closest("[data-raw]");
    if (!token) return;
    input.value = input.value.split(/[\s　]+/).filter(p => p && p !== token.dataset.raw).join(" ");
    run();
    input.focus({ preventScroll: true });
  });

  grid.addEventListener("click", e => {
    const tile = e.target.closest(".tile");
    if (!tile) return;
    gridMode = true;
    select(+tile.dataset.i, false);
  });
  grid.addEventListener("dblclick", e => {
    const tile = e.target.closest(".tile");
    if (!tile) return;
    footSay(`双击：打开 ${results[+tile.dataset.i].name}（演示里不会真的打开）`);
  });
  grid.addEventListener("dragstart", e => {
    const tile = e.target.closest(".tile");
    if (!tile) return;
    const item = results[+tile.dataset.i];
    e.dataTransfer.effectAllowed = "copy";
    e.dataTransfer.setData("text/plain", item.name);
    const img = tile.querySelector(".tile-thumb");
    if (img) e.dataTransfer.setDragImage(img, 60, 60);
    select(+tile.dataset.i, false);
  });
  grid.addEventListener("dragend", e => {
    const tile = e.target.closest(".tile");
    if (!tile) return;
    flash(+tile.dataset.i);
    footSay("在软件里，松手就是复制一份过去，原文件没动");
  });

  $$("[data-try]").forEach(button => button.addEventListener("click", () => {
    userTouched = true;
    stopAutoType();
    category = "all";
    input.value = button.dataset.try;
    run();
    input.focus({ preventScroll: true });
  }));

  // 网页里按 / 或 Ctrl+Alt+Space：滚到演示并聚焦
  document.addEventListener("keydown", e => {
    const typing = /^(INPUT|TEXTAREA|SELECT)$/.test(document.activeElement?.tagName || "");
    if ((e.key === "/" && !typing) || (e.ctrlKey && e.altKey && e.code === "Space")) {
      e.preventDefault();
      $("#demo-stage").scrollIntoView({ behavior: reduceMotion ? "auto" : "smooth", block: "center" });
      input.focus({ preventScroll: true });
      input.select();
    }
  });

  // 首屏唯一的自动动作：一秒后替你输入“14-5”，展示一次完整的展开过程
  let autoTimers = [];
  function stopAutoType() { autoTimers.forEach(clearTimeout); autoTimers = []; }
  function autoType() {
    if (reduceMotion || userTouched) return;
    const text = "14-5";
    [...text].forEach((_, i) => autoTimers.push(setTimeout(() => {
      if (userTouched) return;
      input.value = text.slice(0, i + 1);
      run();
    }, 1300 + i * 170)));
  }
  autoType();
  updateFoot();

  // ================= 缩放：预览和功能区共用 =================

  class Zoomer {
    constructor(canvas, img, onChange) {
      this.canvas = canvas;
      this.img = img;
      this.onChange = onChange || (() => {});
      this.z = 1; this.x = 0; this.y = 0;
      this.fit = { x: 0, y: 0, w: 0, h: 0 };
      this.maxZoom = 6;
      this.active = () => true;
      canvas.addEventListener("wheel", e => {
        if (!this.active()) return;
        e.preventDefault();
        const r = canvas.getBoundingClientRect();
        // 一格滚轮约 1.2 倍；精确触控板的小增量也能连续缩放
        this.zoomAt(e.clientX - r.left, e.clientY - r.top, this.z * Math.pow(1.0016, -e.deltaY * (e.deltaMode === 1 ? 33 : 1)), true);
      }, { passive: false });
      canvas.addEventListener("dblclick", e => {
        if (!this.active()) return;
        e.preventDefault();
        const r = canvas.getBoundingClientRect();
        if (this.z > 1.01) this.reset(true);
        else this.zoomAt(e.clientX - r.left, e.clientY - r.top, this.actualZoom(), true);
      });
      let drag = null;
      canvas.addEventListener("pointerdown", e => {
        if (!this.active() || this.z <= 1.01 || e.button !== 0) return;
        drag = { id: e.pointerId, sx: e.clientX, sy: e.clientY, x: this.x, y: this.y };
        canvas.setPointerCapture(e.pointerId);
        canvas.classList.add("panning");
      });
      canvas.addEventListener("pointermove", e => {
        if (!drag || e.pointerId !== drag.id) return;
        this.x = drag.x + e.clientX - drag.sx;
        this.y = drag.y + e.clientY - drag.sy;
        this.clamp();
        this.apply(false);
      });
      const end = () => { drag = null; canvas.classList.remove("panning"); };
      canvas.addEventListener("pointerup", end);
      canvas.addEventListener("pointercancel", end);
    }
    actualZoom() { return Math.max(2, Math.min(this.maxZoom, (this.realWidth || 0) / Math.max(1, this.fit.w))); }
    layout(naturalW, naturalH, realWidth, cap = Infinity) {
      const W = this.canvas.clientWidth, H = this.canvas.clientHeight, pad = 28;
      const s = Math.min((W - pad * 2) / naturalW, (H - pad * 2) / naturalH, cap);
      const w = naturalW * s, h = naturalH * s;
      this.fit = { x: (W - w) / 2, y: (H - h) / 2, w, h };
      this.realWidth = realWidth || naturalW;
      this.z = 1; this.x = 0; this.y = 0;
      this.apply(false);
    }
    zoomAt(px, py, z, animate) {
      z = Math.max(1, Math.min(this.maxZoom, z));
      // 鼠标下面那个点缩放后还在鼠标下面
      const u = (px - this.fit.x - this.x) / this.z, v = (py - this.fit.y - this.y) / this.z;
      this.x = px - this.fit.x - u * z;
      this.y = py - this.fit.y - v * z;
      this.z = z;
      if (this.z <= 1.001) { this.z = 1; this.x = 0; this.y = 0; }
      this.clamp();
      this.apply(animate);
    }
    reset(animate) { this.z = 1; this.x = 0; this.y = 0; this.apply(animate); }
    clamp() {
      const W = this.canvas.clientWidth, H = this.canvas.clientHeight;
      const w = this.fit.w * this.z, h = this.fit.h * this.z;
      this.x = w <= W ? (W - w) / 2 - this.fit.x : Math.min(-this.fit.x, Math.max(W - this.fit.x - w, this.x));
      this.y = h <= H ? (H - h) / 2 - this.fit.y : Math.min(-this.fit.y, Math.max(H - this.fit.y - h, this.y));
      if (this.z <= 1) { this.x = 0; this.y = 0; }
    }
    apply(animate) {
      // 直接改位置和尺寸而不是 CSS scale：图片按放大后的尺寸重新绘制，放大也清楚
      const ease = "cubic-bezier(.33,1,.68,1)";
      this.img.style.transition = animate && !reduceMotion ? ["left", "top", "width", "height"].map(p => `${p} .14s ${ease}`).join(", ") : "none";
      Object.assign(this.img.style, {
        left: `${this.fit.x + this.x}px`, top: `${this.fit.y + this.y}px`,
        width: `${this.fit.w * this.z}px`, height: `${this.fit.h * this.z}px`
      });
      this.canvas.classList.toggle("zoomed", this.z > 1.01);
      this.onChange(this);
    }
  }

  // ================= 空格预览（共享元素转场） =================

  const look = $("#look");
  const lookCanvas = $("#look-canvas");
  const lookImg = $("#look-img");
  let lookOpen = false;
  let lookIndex = -1;
  const lookZoom = new Zoomer(lookCanvas, lookImg, z => {
    $("#look-zoom").textContent = z.z <= 1.01 ? "适应" : `${Math.round(z.fit.w * z.z / z.realWidth * 100)}%`;
  });

  function lookSource(item) {
    // 预览用更大的矢量图：放大也清楚（真实软件里会换成高清大图）
    if (item.folder) return null;
    const ratio = item.kind === "detail" ? [1, 1] : [1, 1];
    return { src: art(item, 1200, 1200), w: 1200 * ratio[0], h: 1200 * ratio[1] };
  }

  function placeLook(i, fromTile) {
    const item = results[i];
    const source = lookSource(item);
    lookIndex = i;
    $("#look-name").textContent = item.name;
    $("#look-meta").textContent = [TYPE[item.ext]?.[2] || "文件夹", item.w ? `${item.w} × ${item.h}` : null, item.folder ? null : sizeLabel(item.mb), dateLabel(item.modified)].filter(Boolean).join("  ·  ");
    if (!source) { lookImg.removeAttribute("src"); lookImg.style.opacity = 0; return; }
    lookImg.style.opacity = 1;
    lookImg.src = source.src;
    lookZoom.layout(source.w, source.h, item.w || source.w, 1);
    if (fromTile && !reduceMotion) {
      // 从网格里那张缩略图的位置飞出来
      const thumb = grid.querySelector(`.tile[data-i="${i}"] .tile-thumb img`);
      if (thumb) {
        const a = thumb.getBoundingClientRect(), c = lookCanvas.getBoundingClientRect(), f = lookZoom.fit;
        const sx = a.width / f.w, sy = a.height / f.h;
        lookImg.animate([
          { transform: `translate(${a.left - c.left - f.x}px, ${a.top - c.top - f.y}px) scale(${sx}, ${sy})` },
          { transform: "translate(0px, 0px) scale(1)" }
        ], { duration: 320, easing: "cubic-bezier(.16,1,.3,1)" });
      }
    }
  }

  function openLook(i) {
    if (i < 0 || !results[i]) return;
    lookOpen = true;
    look.hidden = false;
    look.classList.add("fade-in");
    placeLook(i, true);
  }

  function closeLook() {
    if (!lookOpen) return;
    lookOpen = false;
    const thumb = grid.querySelector(`.tile[data-i="${lookIndex}"] .tile-thumb img`);
    const finish = () => { look.hidden = true; look.classList.remove("fade-in"); };
    if (thumb && lookZoom.z <= 1.01 && !reduceMotion && lookImg.getAttribute("src")) {
      const a = thumb.getBoundingClientRect(), c = lookCanvas.getBoundingClientRect(), f = lookZoom.fit;
      const anim = lookImg.animate([
        { transform: "translate(0px, 0px) scale(1)" },
        { transform: `translate(${a.left - c.left - f.x}px, ${a.top - c.top - f.y}px) scale(${a.width / f.w}, ${a.height / f.h})` }
      ], { duration: 240, easing: "cubic-bezier(.16,1,.3,1)" });
      look.animate([{ backgroundColor: "rgba(250,250,252,.97)" }, { backgroundColor: "rgba(250,250,252,0)" }], { duration: 240 });
      anim.onfinish = finish;
    } else finish();
    input.focus({ preventScroll: true });
  }

  document.addEventListener("keydown", e => {
    if (!lookOpen) return;
    const cols = columns();
    const step = { ArrowLeft: -1, ArrowRight: 1, ArrowUp: -cols, ArrowDown: cols }[e.key];
    if (e.key === " " || e.key === "Escape") { e.preventDefault(); closeLook(); }
    else if (step) {
      e.preventDefault();
      const next = Math.max(0, Math.min(results.length - 1, lookIndex + step));
      if (next !== lookIndex) { select(next); placeLook(next, false); }
    }
    else if (e.key === "Enter") { e.preventDefault(); closeLook(); footSay(`打开所在位置：${results[current].dir}`); }
    else if (e.key === "+" || e.key === "=") { e.preventDefault(); lookZoom.zoomAt(lookCanvas.clientWidth / 2, lookCanvas.clientHeight / 2, lookZoom.z * 1.25, true); }
    else if (e.key === "-") { e.preventDefault(); lookZoom.zoomAt(lookCanvas.clientWidth / 2, lookCanvas.clientHeight / 2, lookZoom.z / 1.25, true); }
    else if (e.key === "0") { e.preventDefault(); lookZoom.reset(true); }
  });
  lookCanvas.addEventListener("click", e => { if (e.target === lookCanvas && lookZoom.z <= 1.01) closeLook(); });
  grid.addEventListener("keydown", e => { if (e.key === " ") e.preventDefault(); });

  // ================= 功能区：可以试的缩放 =================

  const zoomDemo = $("#zoom-demo");
  const zoomImg = $("#zoom-img");
  const zoomTag = $("#zoom-tag");
  let zoomActive = false;
  const zoomSample = { kind: "white", prod: "jar", hue: "violet" };
  zoomImg.src = art(zoomSample, 1400, 1400);
  const finePointer = window.matchMedia("(pointer: fine)").matches;
  if (!finePointer) zoomTag.textContent = "在电脑上可以用滚轮缩放、双击看原始尺寸";
  const demoZoomer = new Zoomer(zoomDemo, zoomImg, z => {
    if (!zoomActive) return;
    zoomTag.textContent = z.z <= 1.01 ? "适应窗口 · 滚轮放大，双击原始尺寸" : `${Math.round(z.fit.w * z.z / 2400 * 100)}% · 按住拖动`;
  });
  demoZoomer.maxZoom = 8;
  demoZoomer.active = () => zoomActive;
  const layoutDemo = () => demoZoomer.layout(1400, 1400, 2400);
  zoomImg.addEventListener("load", layoutDemo, { once: true });
  window.addEventListener("resize", () => { layoutDemo(); scaleDrag(); });
  const activateZoom = () => {
    if (zoomActive) return;
    zoomActive = true;
    zoomDemo.classList.add("active");
    demoZoomer.apply(false);
  };
  zoomDemo.addEventListener("click", activateZoom);
  zoomDemo.addEventListener("focus", activateZoom);
  // 离开就还给页面滚动，避免滚轮被“劫持”
  zoomDemo.addEventListener("mouseleave", () => {
    if (!zoomActive) return;
    zoomActive = false;
    zoomDemo.classList.remove("active");
    demoZoomer.reset(true);
    zoomTag.textContent = finePointer ? "点一下，再用滚轮缩放" : "在电脑上可以用滚轮缩放、双击看原始尺寸";
  });

  // ================= 功能区：工作台缩略 =================

  const benchGrid = $("#bench-grid");
  const benchItems = ITEMS.filter(i => !i.folder && /14[-_]?5/i.test(i.name)).concat(ITEMS.filter(i => i.name.startsWith("BX")));
  benchGrid.innerHTML = benchItems.slice(0, 12).map((item, i) => `<span${i === 1 ? ' class="sel"' : ""} style="background-image:url(&quot;${item.thumb}&quot;)"></span>`).join("");
  $("#bench-preview").style.backgroundImage = `url("${ITEMS[1].thumb}")`;

  // ================= 滚动到才播放 =================

  const drag = $(".drag-demo");
  const layers = $(".layers");
  const io = new IntersectionObserver(entries => entries.forEach(entry => {
    if (entry.target === drag) drag.classList.toggle("playing", entry.isIntersecting);
    if (entry.target === layers && entry.isIntersecting) layers.classList.add("seen");
  }), { threshold: .35 });
  io.observe(drag);
  io.observe(layers);

  // 拖拽插画按 560 × 360 设计，窄屏整体等比缩小
  const dragStage = $(".drag-stage");
  function scaleDrag() {
    if (!dragStage) return;
    const s = Math.min(1, drag.clientWidth / 560);
    dragStage.style.transform = `scale(${s})`;
    drag.style.height = `${360 * s}px`;
  }
  scaleDrag();
})();
