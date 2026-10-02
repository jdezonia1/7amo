// Raffaello subcontractor portal - plain JavaScript, no external libraries (strict CSP: script-src 'self').
// All text that comes from the server is written with textContent (never innerHTML).
(function () {
  "use strict";

  var API = "/api/v1/portal";
  var EXT = [".xlsx", ".pdf", ".jpg", ".jpeg", ".png", ".heic"];
  var state = { token: null, me: null, lang: "en", tab: "home", claims: [], remaining: [], pendingFiles: [] };

  var T = {
    en: {
      appName: "RAFFAELLO", portal: "SUBCONTRACTOR PORTAL", logout: "SIGN OUT", signIn: "SIGN IN", userName: "User name", password: "Password",
      signInBtn: "SIGN IN", loginHelp: "Accounts are issued by the MOBCO QS team. You only ever see your own company's data.",
      tabHome: "HOME", tabStatement: "SEND STATEMENT", tabSubmissions: "MY SUBMISSIONS", tabInvoices: "CLAIMS & INVOICES", tabRemaining: "REMAINING", tabMessages: "MESSAGES",
      lastSubmission: "LAST SUBMISSION", openInvoices: "INVOICES", unreadMessages: "UNREAD MESSAGES", howTitle: "HOW IT WORKS",
      how1: "Download your standard site statement template (your rooms only).", how2: "Fill the quantities per room and stage, SITE %, WIR % and WIR no.",
      how3: "Send it here with the marked-up drawings (PDF) and photos.", how4: "Follow the status: submitted, under review, imported or rejected with the reason.",
      templateTitle: "1. STATEMENT TEMPLATE", templateHelp: "A new numbered template with your rooms and the remaining quantities as cell comments.",
      downloadTemplate: "DOWNLOAD TEMPLATE (.XLSX)", sendTitle: "2. SEND FILLED STATEMENT", statementNo: "Statement no. (optional - read from the file)",
      note: "Note to the QS", files: "Statement (.xlsx), drawings (.pdf), photos (.jpg / .png / .heic)", camera: "Take a photo", send: "SEND",
      invoicesTitle: "INVOICES", claimsTitle: "CLAIM LINES", filter: "Filter", colInvoice: "INVOICE", colStatus: "STATUS", colReason: "REASON",
      colCurrent: "THIS INVOICE (SAR)", colCumulative: "TO DATE (SAR)", colDate: "DATE", colInv: "INV", colRoom: "ROOM", colStage: "STAGE", colItem: "ITEM",
      colQty: "QTY", colWir: "WIR NO", colChecks: "CHECKS", remainingTitle: "REMAINING QUANTITIES PER ROOM",
      remainingHelp: "PROJECT QTY minus everything already claimed on the room (all subcontractors). Claims above it are checked by the QS.",
      colLevel: "LEVEL", colProject: "PROJECT QTY", colYours: "CLAIMED BY YOU", colRemaining: "REMAINING", subject: "Subject", message: "Message",
      sendMessage: "SEND MESSAGE", footer: "MOBCO - Raffles project. Every action on this portal is recorded.",
      st_submitted: "SUBMITTED", st_under_review: "UNDER REVIEW", st_imported: "IMPORTED", st_rejected: "REJECTED", st_draft: "DRAFT", st_approved: "APPROVED",
      none: "Nothing yet.", files_n: "file(s)", lines: "lines", qty: "qty", sending: "Sending...", sent: "Sent - submission #", tooBig: "too large",
      badType: "not allowed", chooseFiles: "Choose at least one file.", oneStatement: "Only one statement workbook per submission.",
      sessionEnded: "Your session ended - sign in again.", you: "You", qs: "QS", download: "download", reviewed: "Reviewed", invoice: "Invoice",
      findings: "Checks at upload", noRooms: "No rows.", remove: "remove", revision: "Rev"
    },
    ar: {
      appName: "رافاييلو", portal: "بوابة مقاولي الباطن", logout: "تسجيل الخروج", signIn: "تسجيل الدخول", userName: "اسم المستخدم", password: "كلمة المرور",
      signInBtn: "دخول", loginHelp: "يتم إصدار الحسابات من فريق حصر الكميات في موبكو. لا ترى إلا بيانات شركتك فقط.",
      tabHome: "الرئيسية", tabStatement: "إرسال المستخلص", tabSubmissions: "طلباتي", tabInvoices: "المطالبات والفواتير", tabRemaining: "الكميات المتبقية", tabMessages: "الرسائل",
      lastSubmission: "آخر طلب", openInvoices: "الفواتير", unreadMessages: "رسائل غير مقروءة", howTitle: "طريقة العمل",
      how1: "حمّل نموذج كشف الموقع الخاص بك (غرفك فقط).", how2: "أدخل الكميات لكل غرفة ومرحلة، ونسبة الموقع ونسبة WIR ورقم WIR.",
      how3: "أرسله هنا مع المخططات المؤشرة (PDF) والصور.", how4: "تابع الحالة: مُرسل، قيد المراجعة، تم الإدخال، أو مرفوض مع السبب.",
      templateTitle: "١. نموذج الكشف", templateHelp: "نموذج جديد مرقّم بغرفك والكميات المتبقية كتعليقات في الخلايا.",
      downloadTemplate: "تحميل النموذج (XLSX)", sendTitle: "٢. إرسال الكشف المعبأ", statementNo: "رقم الكشف (اختياري - يُقرأ من الملف)",
      note: "ملاحظة لمهندس الكميات", files: "الكشف (xlsx)، المخططات (pdf)، الصور (jpg / png / heic)", camera: "التقاط صورة", send: "إرسال",
      invoicesTitle: "الفواتير", claimsTitle: "بنود المطالبة", filter: "تصفية", colInvoice: "الفاتورة", colStatus: "الحالة", colReason: "السبب",
      colCurrent: "هذه الفاتورة (ريال)", colCumulative: "حتى تاريخه (ريال)", colDate: "التاريخ", colInv: "فاتورة", colRoom: "الغرفة", colStage: "المرحلة", colItem: "البند",
      colQty: "الكمية", colWir: "رقم WIR", colChecks: "التدقيق", remainingTitle: "الكميات المتبقية لكل غرفة",
      remainingHelp: "كمية المشروع ناقص كل ما تمت المطالبة به في الغرفة (جميع المقاولين). المطالبات الزائدة يدققها مهندس الكميات.",
      colLevel: "الدور", colProject: "كمية المشروع", colYours: "مطالبتك", colRemaining: "المتبقي", subject: "الموضوع", message: "الرسالة",
      sendMessage: "إرسال الرسالة", footer: "موبكو - مشروع رافلز. يتم تسجيل كل إجراء على هذه البوابة.",
      st_submitted: "مُرسل", st_under_review: "قيد المراجعة", st_imported: "تم الإدخال", st_rejected: "مرفوض", st_draft: "مسودة", st_approved: "معتمد",
      none: "لا يوجد شيء بعد.", files_n: "ملف", lines: "بند", qty: "كمية", sending: "جارٍ الإرسال...", sent: "تم الإرسال - طلب رقم ", tooBig: "كبير جداً",
      badType: "غير مسموح", chooseFiles: "اختر ملفاً واحداً على الأقل.", oneStatement: "كشف واحد فقط لكل طلب.",
      sessionEnded: "انتهت الجلسة - سجّل الدخول مرة أخرى.", you: "أنت", qs: "الكميات", download: "تحميل", reviewed: "روجع", invoice: "فاتورة",
      findings: "ملاحظات عند الرفع", noRooms: "لا توجد بيانات.", remove: "إزالة", revision: "مراجعة"
    }
  };

  function t(key) { return (T[state.lang] && T[state.lang][key]) || T.en[key] || key; }
  function $(id) { return document.getElementById(id); }
  function el(tag, cls, text) { var e = document.createElement(tag); if (cls) e.className = cls; if (text !== undefined && text !== null) e.textContent = String(text); return e; }
  function fmt(n, d) { if (n === null || n === undefined) return ""; return Number(n).toLocaleString(state.lang === "ar" ? "ar-SA" : "en-GB", { minimumFractionDigits: d || 0, maximumFractionDigits: d === undefined ? 2 : d }); }
  function date(s) { if (!s) return ""; var d = new Date(s); return isNaN(d) ? "" : d.toLocaleString(state.lang === "ar" ? "ar-SA" : "en-GB", { day: "2-digit", month: "short", year: "numeric", hour: "2-digit", minute: "2-digit" }); }
  function size(b) { return b >= 1048576 ? (b / 1048576).toFixed(1) + " MB" : Math.max(1, Math.round(b / 1024)) + " KB"; }
  function chip(status) { var s = String(status || "").toLowerCase(); return el("span", "chip " + s, t("st_" + s)); }

  function savedLang() { try { return localStorage.getItem("raffaello-portal-lang"); } catch (e) { return null; } }
  function save(k, v) { try { if (v === null) sessionStorage.removeItem(k); else sessionStorage.setItem(k, v); } catch (e) { /* private mode */ } }
  function load(k) { try { return sessionStorage.getItem(k); } catch (e) { return null; } }

  // ------------------------------------------------------------------ language

  function applyLang() {
    document.documentElement.lang = state.lang;
    document.documentElement.dir = state.lang === "ar" ? "rtl" : "ltr";
    var nodes = document.querySelectorAll("[data-t]");
    for (var i = 0; i < nodes.length; i++) {
      var key = nodes[i].getAttribute("data-t");
      var badge = nodes[i].querySelector(".badge");
      nodes[i].textContent = t(key);
      if (badge) { nodes[i].appendChild(document.createTextNode(" ")); nodes[i].appendChild(badge); }
    }
    $("langBtn").textContent = state.lang === "ar" ? "English" : "عربي";
    if (state.me) render(state.tab);
  }

  // ------------------------------------------------------------------ API

  function api(method, path, body, raw) {
    var headers = {};
    if (state.token) headers["Authorization"] = "Bearer " + state.token;
    if (body && !(body instanceof FormData)) headers["Content-Type"] = "application/json";
    return fetch(API + path, { method: method, headers: headers, body: body ? (body instanceof FormData ? body : JSON.stringify(body)) : undefined, credentials: "omit" })
      .then(function (r) {
        if (r.status === 401 && state.token) { signOut(t("sessionEnded")); throw new Error(t("sessionEnded")); }
        if (!r.ok) return r.json().catch(function () { return {}; }).then(function (e) { throw new Error(e.Message || (r.status + " " + r.statusText)); });
        if (raw) return r;
        if (r.status === 204) return null;
        return r.json();
      });
  }

  function downloadBlob(path, fallbackName) {
    return api("GET", path, null, true).then(function (r) {
      var cd = r.headers.get("Content-Disposition") || "";
      var m = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(cd);
      var name = m ? decodeURIComponent(m[1]) : fallbackName;
      return r.blob().then(function (b) {
        var url = URL.createObjectURL(b);
        var a = el("a"); a.href = url; a.download = name; document.body.appendChild(a); a.click();
        setTimeout(function () { URL.revokeObjectURL(url); a.remove(); }, 1000);
      });
    });
  }

  // ------------------------------------------------------------------ sign in / out

  function signIn(ev) {
    ev.preventDefault();
    $("loginError").textContent = "";
    var body = { UserName: $("loginUser").value.trim(), Password: $("loginPass").value };
    api("POST", "/login", body).then(function (r) {
      state.token = r.Token; save("raffaello-portal-token", r.Token);
      if (r.Language && !savedLang()) { state.lang = r.Language === "ar" ? "ar" : "en"; applyLang(); }
      $("loginPass").value = "";
      return start();
    }).catch(function (e) { $("loginError").textContent = e.message; });
  }

  function signOut(msg) {
    var tok = state.token;
    state.token = null; state.me = null; save("raffaello-portal-token", null);
    if (tok) fetch(API + "/logout", { method: "POST", headers: { Authorization: "Bearer " + tok } }).catch(function () { });
    $("mainView").classList.add("hidden"); $("loginView").classList.remove("hidden");
    $("logoutBtn").classList.add("hidden"); $("who").textContent = "";
    if (msg) $("loginError").textContent = msg;
  }

  function start() {
    return api("GET", "/me").then(function (me) {
      state.me = me;
      $("who").textContent = me.DisplayName + " - " + (me.CompanyDisplayName || me.Company);
      $("loginView").classList.add("hidden"); $("mainView").classList.remove("hidden"); $("logoutBtn").classList.remove("hidden");
      setUnread(me.UnreadMessages);
      show(state.tab);
    });
  }

  function setUnread(n) { var b = $("unread"); b.textContent = n; b.classList.toggle("hidden", !n); $("homeUnread").textContent = n || 0; }

  // ------------------------------------------------------------------ tabs

  function show(tab) {
    state.tab = tab;
    var buttons = document.querySelectorAll("#tabs button");
    for (var i = 0; i < buttons.length; i++) buttons[i].classList.toggle("active", buttons[i].getAttribute("data-tab") === tab);
    var tabs = document.querySelectorAll(".tab");
    for (var j = 0; j < tabs.length; j++) tabs[j].classList.toggle("hidden", tabs[j].id !== "tab-" + tab);
    render(tab);
  }

  function render(tab) {
    if (tab === "home") return home();
    if (tab === "submissions") return submissions();
    if (tab === "invoices") return invoices();
    if (tab === "remaining") return remaining();
    if (tab === "messages") return messages();
    if (tab === "statement") return renderPending();
  }

  function home() {
    api("GET", "/submissions").then(function (list) {
      if (!list.length) { $("homeLast").textContent = t("none"); $("homeLastStatus").textContent = ""; return; }
      $("homeLast").textContent = (list[0].StatementNo || "#" + list[0].Id);
      var s = $("homeLastStatus"); s.textContent = ""; s.appendChild(chip(list[0].Status));
    }).catch(noop);
    api("GET", "/invoices").then(function (list) {
      $("homeInvoices").textContent = list.length;
      var s = $("homeInvoiceStatus"); s.textContent = "";
      if (list.length) { s.appendChild(document.createTextNode("INV-" + pad(list[0].InvoiceNo) + " " + t("revision") + " " + list[0].Revision + " ")); s.appendChild(chip(list[0].Status)); }
    }).catch(noop);
  }

  function pad(n) { return (n < 10 ? "0" : "") + n; }
  function noop() { }

  function submissions() {
    var box = $("submissionList");
    api("GET", "/submissions").then(function (list) {
      box.textContent = "";
      if (!list.length) { box.appendChild(el("p", "card", t("none"))); return; }
      list.forEach(function (s) {
        var c = el("div", "card");
        var h = el("h3"); h.appendChild(chip(s.Status)); h.appendChild(el("span", null, (s.StatementNo || "") + "  #" + s.Id)); c.appendChild(h);
        c.appendChild(el("div", "small", date(s.SubmittedAt) + " - " + s.SubmittedBy + " - " + s.Files + " " + t("files_n") + " (" + size(s.TotalBytes) + ")" +
          (s.StatementLines ? " - " + s.StatementLines + " " + t("lines") + ", " + t("qty") + " " + fmt(s.StatementQty) : "")));
        if (s.Note) c.appendChild(el("p", null, s.Note));
        if (s.Findings) c.appendChild(el("p", "small", t("findings") + ": " + s.Findings));
        if (s.Reason) c.appendChild(el("div", "reason", s.Reason));
        if (s.ReviewedAt) c.appendChild(el("div", "small", t("reviewed") + " " + date(s.ReviewedAt) + (s.InvoiceNo ? " - " + t("invoice") + " " + s.InvoiceNo : "")));
        var files = el("ul", "filelist"); c.appendChild(files);
        api("GET", "/submissions/" + s.Id + "/files").then(function (fs) {
          fs.forEach(function (f) {
            var li = el("li"); li.appendChild(el("span", null, f.FileName + " (" + size(f.Size) + ")"));
            var b = el("button", "link", t("download")); b.type = "button";
            b.addEventListener("click", function () { downloadBlob("/files/" + f.Id, f.FileName).catch(function (e) { alert(e.message); }); });
            li.appendChild(b); files.appendChild(li);
          });
        }).catch(noop);
        box.appendChild(c);
      });
    }).catch(function (e) { box.textContent = e.message; });
  }

  function invoices() {
    api("GET", "/invoices").then(function (list) {
      var tb = $("invoiceTable").tBodies[0]; tb.textContent = "";
      if (!list.length) { var r0 = tb.insertRow(); var c0 = r0.insertCell(); c0.colSpan = 6; c0.textContent = t("none"); }
      list.forEach(function (i) {
        var r = tb.insertRow();
        r.insertCell().textContent = (i.ContractNo ? i.ContractNo + " " : "") + "INV-" + pad(i.InvoiceNo) + " " + t("revision") + " " + i.Revision;
        r.insertCell().appendChild(chip(i.Status));
        r.insertCell().textContent = i.Reason || "";
        var a = r.insertCell(); a.className = "num"; a.textContent = fmt(i.CurrentAmount, 2);
        var b = r.insertCell(); b.className = "num"; b.textContent = fmt(i.CumulativeAmount, 2);
        r.insertCell().textContent = date(i.ApprovedAt || i.SubmittedAt || i.CreatedAt);
      });
    }).catch(noop);
    api("GET", "/claims").then(function (list) { state.claims = list; drawClaims(); }).catch(noop);
  }

  function drawClaims() {
    var f = ($("claimFilter").value || "").toUpperCase().split(",").map(function (x) { return x.trim(); }).filter(Boolean);
    var tb = $("claimTable").tBodies[0]; tb.textContent = "";
    var rows = state.claims.filter(function (c) {
      var hay = ("INV " + c.InvoiceNo + " " + c.Room + " " + c.Stage + " " + c.Item + " " + c.WirNo).toUpperCase();
      return f.every(function (w) { return hay.indexOf(w) >= 0; });
    }).slice(0, 1500);
    if (!rows.length) { var r0 = tb.insertRow(); var c0 = r0.insertCell(); c0.colSpan = 9; c0.textContent = t("noRooms"); return; }
    rows.forEach(function (c) {
      var r = tb.insertRow();
      [c.InvoiceNo, c.Room, c.Stage, c.Item].forEach(function (v) { r.insertCell().textContent = v; });
      var q = r.insertCell(); q.className = "num"; q.textContent = fmt(c.Qty);
      var s = r.insertCell(); s.className = "num"; s.textContent = Math.round(c.SitePct * 100) + "%";
      var w = r.insertCell(); w.className = "num"; w.textContent = Math.round(c.WirPct * 100) + "%";
      r.insertCell().textContent = c.WirNo;
      r.insertCell().textContent = [c.IsOver ? "OVER" : "", c.HeightStatus ? ">4.5m " + c.HeightStatus : "", c.LengthStatus ? "15m " + c.LengthStatus : ""].filter(Boolean).join("  ");
    });
  }

  function remaining() {
    api("GET", "/remaining").then(function (list) { state.remaining = list; drawRemaining(); }).catch(noop);
  }

  function drawRemaining() {
    var f = ($("remFilter").value || "").toUpperCase().split(",").map(function (x) { return x.trim(); }).filter(Boolean);
    var tb = $("remTable").tBodies[0]; tb.textContent = "";
    var rows = state.remaining.filter(function (r) {
      var hay = (r.Room + " " + r.Level + " " + r.Stage + " " + r.Item + " " + r.RoomType).toUpperCase();
      return f.every(function (w) { return hay.indexOf(w) >= 0; });
    }).slice(0, 3000);
    if (!rows.length) { var r0 = tb.insertRow(); var c0 = r0.insertCell(); c0.colSpan = 7; c0.textContent = t("noRooms"); return; }
    rows.forEach(function (x) {
      var r = tb.insertRow(); if (x.Remaining <= 0) r.className = "zero";
      [x.Room, x.Level, x.Stage, x.Item].forEach(function (v) { r.insertCell().textContent = v; });
      [x.ProjectQty, x.ClaimedByYou, x.Remaining].forEach(function (v) { var c = r.insertCell(); c.className = "num"; c.textContent = fmt(v); });
    });
  }

  function messages() {
    api("GET", "/messages").then(function (list) {
      var box = $("thread"); box.textContent = "";
      if (!list.length) box.appendChild(el("p", "small", t("none")));
      var unread = 0;
      list.forEach(function (m) {
        var mine = m.Direction === "FROM_SUB";
        var d = el("div", "msg " + (mine ? "from_sub" : "to_sub") + (!mine && !m.ReadAt ? " unread" : ""));
        d.appendChild(el("div", "meta", (mine ? t("you") + " (" + m.From + ")" : t("qs") + " (" + m.From + ")") + " - " + date(m.SentAt)));
        if (m.Subject) d.appendChild(el("div", "subject", m.Subject));
        d.appendChild(el("div", "body", m.Body));
        box.appendChild(d);
        if (!mine && !m.ReadAt) { unread++; api("POST", "/messages/" + m.Id + "/read").catch(noop); }
      });
      box.scrollTop = box.scrollHeight;
      if (unread) setUnread(0);
    }).catch(noop);
  }

  function sendMessage(ev) {
    ev.preventDefault();
    $("msgError").textContent = "";
    api("POST", "/messages", { Subject: $("msgSubject").value, Body: $("msgBody").value }).then(function () {
      $("msgSubject").value = ""; $("msgBody").value = ""; messages();
    }).catch(function (e) { $("msgError").textContent = e.message; });
  }

  // ------------------------------------------------------------------ statement

  function template() {
    var b = $("templateBtn"); b.disabled = true;
    downloadBlob("/template", "statement.xlsx").catch(function (e) { alert(e.message); }).then(function () { b.disabled = false; });
  }

  function addFiles(list) {
    for (var i = 0; i < list.length; i++) state.pendingFiles.push(list[i]);
    renderPending();
  }

  function check(f) {
    var name = f.name.toLowerCase(); var ext = name.substring(name.lastIndexOf("."));
    if (EXT.indexOf(ext) < 0) return t("badType");
    if (state.me && f.size > state.me.MaxFileBytes) return t("tooBig");
    return "";
  }

  function renderPending() {
    var ul = $("fileList"); ul.textContent = "";
    state.pendingFiles.forEach(function (f, i) {
      var problem = check(f);
      var li = el("li", problem ? "bad" : null); li.appendChild(el("span", null, f.name + " (" + size(f.size) + ")" + (problem ? " - " + problem : "")));
      var b = el("button", "link", t("remove")); b.type = "button";
      b.addEventListener("click", function () { state.pendingFiles.splice(i, 1); renderPending(); });
      li.appendChild(b); ul.appendChild(li);
    });
  }

  function submit(ev) {
    ev.preventDefault();
    $("sendError").textContent = ""; $("sendOk").textContent = "";
    var files = state.pendingFiles;
    if (!files.length) { $("sendError").textContent = t("chooseFiles"); return; }
    var bad = files.filter(function (f) { return check(f); });
    if (bad.length) { $("sendError").textContent = bad[0].name + ": " + check(bad[0]); return; }
    if (files.filter(function (f) { return /\.xlsx$/i.test(f.name); }).length > 1) { $("sendError").textContent = t("oneStatement"); return; }
    var fd = new FormData();
    fd.append("statementNo", $("statementNo").value.trim());
    fd.append("note", $("note").value.trim());
    files.forEach(function (f) { fd.append("files", f, f.name); });
    var xhr = new XMLHttpRequest();
    var btn = $("sendBtn"); btn.disabled = true; btn.textContent = t("sending");
    $("progress").classList.remove("hidden"); $("progressBar").style.width = "0";
    xhr.upload.addEventListener("progress", function (e) { if (e.lengthComputable) $("progressBar").style.width = Math.round(e.loaded * 100 / e.total) + "%"; });
    xhr.addEventListener("loadend", function () {
      btn.disabled = false; btn.textContent = t("send"); $("progress").classList.add("hidden");
      var body = {}; try { body = JSON.parse(xhr.responseText || "{}"); } catch (e) { /* not json */ }
      if (xhr.status === 401) { signOut(t("sessionEnded")); return; }
      if (xhr.status >= 200 && xhr.status < 300) {
        $("sendOk").textContent = t("sent") + body.Id + (body.Findings ? " - " + body.Findings : "");
        state.pendingFiles = []; renderPending(); $("statementNo").value = ""; $("note").value = ""; $("files").value = ""; $("camera").value = "";
      } else $("sendError").textContent = body.Message || (xhr.status + " " + xhr.statusText);
    });
    xhr.open("POST", API + "/submissions");
    xhr.setRequestHeader("Authorization", "Bearer " + state.token);
    xhr.send(fd);
  }

  // ------------------------------------------------------------------ wiring

  document.addEventListener("DOMContentLoaded", function () {
    state.lang = savedLang() || ((navigator.language || "en").substring(0, 2) === "ar" ? "ar" : "en");
    if (state.lang !== "ar") state.lang = "en";
    applyLang();
    $("langBtn").addEventListener("click", function () {
      state.lang = state.lang === "ar" ? "en" : "ar";
      try { localStorage.setItem("raffaello-portal-lang", state.lang); } catch (e) { /* private mode */ }
      applyLang();
    });
    $("loginForm").addEventListener("submit", signIn);
    $("logoutBtn").addEventListener("click", function () { signOut(); });
    $("tabs").addEventListener("click", function (e) { var b = e.target.closest("button[data-tab]"); if (b) show(b.getAttribute("data-tab")); });
    $("templateBtn").addEventListener("click", template);
    $("files").addEventListener("change", function (e) { addFiles(e.target.files); e.target.value = ""; });
    $("camera").addEventListener("change", function (e) { addFiles(e.target.files); e.target.value = ""; });
    $("submitForm").addEventListener("submit", submit);
    $("msgForm").addEventListener("submit", sendMessage);
    $("claimFilter").addEventListener("input", drawClaims);
    $("remFilter").addEventListener("input", drawRemaining);
    state.token = load("raffaello-portal-token");
    if (state.token) start().catch(function () { signOut(); });
  });
})();
