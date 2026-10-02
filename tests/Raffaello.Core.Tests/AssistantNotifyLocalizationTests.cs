using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using ClosedXML.Excel;
using QuestPDF.Fluent;
using Raffaello.Core.Assistant;
using Raffaello.Core.Domain;
using Raffaello.Core.Localization;
using Raffaello.Core.Notify;
using Raffaello.Core.Settings;

namespace Raffaello.Core.Tests;

public class MorningBriefTests
{
    [Fact]
    public void Brief_lists_what_needs_attention_and_compares_with_yesterday()
    {
        using var env = AssistantEnv.Create();
        var p = env.Project;
        var hub = new NotificationHub(env.Store, () => Array.Empty<INotificationChannel>()) { Clock = () => DateTime.Today.AddHours(7.5) };
        // yesterday's brief: no OVER keys known, 1 invoice awaiting
        env.Store.Insert(new BriefSnapshot { Owner = "tester", Date = DateTime.Today.AddDays(-1), BuiltAt = DateTime.Today.AddDays(-1).AddHours(7), MetricsJson = "{\"counts\":{\"OVER_CAP\":0,\"INVOICES\":1},\"keys\":{\"OVER_CAP\":[]}}" });
        env.Store.Insert(new AssistantReminder { Owner = "tester", Due = DateTime.Today.AddHours(9), Text = "Chase WIR for L2", CreatedAt = DateTime.Today.AddDays(-2) });
        var brief = hub.BuildBrief("tester", p.Snapshot, p.Queue, "en");

        var over = brief.Sections.Single(s => s.Code == "OVER_CAP");
        Assert.Equal(1, over.Count);
        Assert.Equal(1, over.Delta);
        Assert.True(over.Items[0].IsNew);
        Assert.Contains("P2-107", over.Items[0].Title);
        var claims = brief.Sections.Single(s => s.Code == "NEW_CLAIMS");
        Assert.Equal(1, claims.Count);                                   // only the claim entered after yesterday's brief
        var inv = brief.Sections.Single(s => s.Code == "INVOICES");
        Assert.Contains(inv.Items, i => i.Title.Contains("ROOTS INV-01 Rev 0") && i.Detail.Contains("WIR missing"));
        Assert.Contains(brief.Sections.Single(s => s.Code == "OBLIGATIONS").Items, i => i.Title == "Chase WIR for L2");
        Assert.Equal(new[] { "NEW_CLAIMS", "OVER_CAP", "CHECKS", "INVOICES", "ACONEX", "MATERIALS", "VO", "OBLIGATIONS" }, brief.Sections.Select(s => s.Code));

        var text = brief.ToText();
        Assert.Contains("MORNING BRIEF", text);
        Assert.Contains("OVER THE CAP (1, +1)", text);
        Assert.Contains("[NEW] P2-107", text);
        Assert.Contains("<html dir=\"ltr\"", brief.ToHtml());
        var pdf = BriefPdf.Render(brief);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));

        // stored once per day, compared tomorrow
        MorningBriefBuilder.Save(env.Store, brief);
        MorningBriefBuilder.Save(env.Store, brief);
        Assert.Single(env.Store.All<BriefSnapshot>(), b => b.Date == DateTime.Today);
    }

    [Fact]
    public void Arabic_brief_is_right_to_left_with_gregorian_dates_and_renders_to_pdf()
    {
        using var env = AssistantEnv.Create();
        var hub = new NotificationHub(env.Store, () => Array.Empty<INotificationChannel>()) { Clock = () => new DateTime(2026, 10, 2, 7, 30, 0) };
        var brief = hub.BuildBrief("tester", env.Project.Snapshot, env.Project.Queue, "ar");
        var text = brief.ToText();
        Assert.Contains("الموجز الصباحي", text);
        Assert.Contains("2026", text);                                   // Gregorian, not 1448
        Assert.Contains("تجاوز السقف", text);
        Assert.Contains("<html dir=\"rtl\"", brief.ToHtml());
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(BriefPdf.Render(brief), 0, 4));
    }

    [Fact]
    public void Brief_is_due_once_a_day_after_its_time()
    {
        var now = new DateTime(2026, 10, 2, 8, 0, 0);
        Assert.True(NotificationHub.BriefDue("07:30", now, null));
        Assert.False(NotificationHub.BriefDue("08:30", now, null));
        Assert.False(NotificationHub.BriefDue("07:30", now, now.AddHours(-0.5)));
        Assert.True(NotificationHub.BriefDue("07:30", now, now.AddDays(-1)));
        Assert.False(NotificationHub.BriefDue("", now, null));
    }
}

public class NotificationTests
{
    private sealed class Capture : INotificationChannel
    {
        public Capture(string code) => Code = code;
        public string Code { get; }
        public bool IsConfigured => true;
        public List<(NotificationEvent E, string Address)> Sent { get; } = new();
        public Task SendAsync(NotificationEvent e, string address, CancellationToken ct) { Sent.Add((e, address)); return Task.CompletedTask; }
    }

    [Fact]
    public async Task Router_follows_rules_severity_quiet_hours_and_sends_each_event_once()
    {
        var store = new InMemoryAssistantStore("tester");
        store.Insert(new NotificationRule { Owner = "tester", EventKind = NotifyEvents.All, Channel = NotifyChannels.InApp, MinSeverity = "DUE" });
        store.Insert(new NotificationRule { Owner = "tester", EventKind = NotifyEvents.OverCap, Channel = NotifyChannels.Email, MinSeverity = "OVER", QuietFrom = "22:00", QuietTo = "07:00", Address = "qs@example.com" });
        store.Insert(new NotificationRule { Owner = "tester", EventKind = NotifyEvents.OverCap, Channel = NotifyChannels.Teams, Enabled = false });
        var inApp = new Capture(NotifyChannels.InApp);
        var mail = new Capture(NotifyChannels.Email);
        var teams = new Capture(NotifyChannels.Teams);
        var now = new DateTime(2026, 10, 2, 23, 0, 0);
        var router = new NotificationRouter(store, new INotificationChannel[] { inApp, mail, teams }) { Clock = () => now };
        var events = new[]
        {
            new NotificationEvent(NotifyEvents.OverCap, "OVER", "OVER: P2-107", "45 of 39", "over|P2-107"),
            new NotificationEvent(NotifyEvents.NewClaims, "OPEN", "ROOTS: 3 new claim lines", "", "claims|ROOTS|9"),
        };
        var r1 = await router.RouteAsync("tester", events);
        Assert.Single(inApp.Sent);                       // OPEN is below DUE
        Assert.Empty(mail.Sent);                         // quiet hours (wrap midnight)
        Assert.Empty(teams.Sent);                        // rule disabled
        now = new DateTime(2026, 10, 3, 7, 5, 0);
        await router.RouteAsync("tester", events);
        Assert.Single(mail.Sent);
        Assert.Equal("qs@example.com", mail.Sent[0].Address);
        Assert.Single(inApp.Sent);                       // not repeated
        await router.RouteAsync("tester", events);
        Assert.Single(mail.Sent);
        Assert.Equal(2, store.All<NotificationLog>().Count);
        Assert.Single(r1);
    }

    [Fact]
    public void Quiet_hours_wrap_midnight()
    {
        var r = new NotificationRule { QuietFrom = "22:00", QuietTo = "06:30" };
        Assert.True(NotificationRouter.InQuietHours(r, new DateTime(2026, 1, 1, 23, 0, 0)));
        Assert.True(NotificationRouter.InQuietHours(r, new DateTime(2026, 1, 1, 6, 0, 0)));
        Assert.False(NotificationRouter.InQuietHours(r, new DateTime(2026, 1, 1, 12, 0, 0)));
        Assert.False(NotificationRouter.InQuietHours(new NotificationRule(), DateTime.Now));
    }

    [Fact]
    public async Task Teams_whatsapp_and_email_payloads()
    {
        var e = new NotificationEvent(NotifyEvents.AconexOverdue, "OVER", "ROOTS INV-01 overdue \"3\" days", "Step 'QS review' with أحمد", "k");
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var http = new HttpClient(handler);
        var teams = new TeamsChannel("https://example.webhook.office.com/x", http);
        Assert.True(teams.IsConfigured);
        Assert.False(new TeamsChannel("http://not-https").IsConfigured);
        await teams.SendAsync(e, "", CancellationToken.None);
        var card = JsonNode.Parse(handler.Bodies[0])!;
        Assert.Equal("application/vnd.microsoft.card.adaptive", (string?)card["attachments"]![0]!["contentType"]);
        Assert.Equal("Attention", (string?)card["attachments"]![0]!["content"]!["body"]![0]!["color"]);

        var wa = new WhatsAppWebhookChannel("https://gw.example.com/send", "{\"to\":\"{to}\",\"body\":\"{title} - {text}\"}", "+966500000000", "Authorization", "Bearer abc", http);
        var body = JsonNode.Parse(wa.Body(e, ""))!;                       // quotes / Arabic escaped -> valid JSON
        Assert.Equal("+966500000000", (string?)body["to"]);
        Assert.Equal("ROOTS INV-01 overdue \"3\" days - Step 'QS review' with أحمد", (string?)body["body"]);
        await wa.SendAsync(e, "", CancellationToken.None);
        Assert.Equal("Bearer abc", handler.Requests[1].Headers.GetValues("Authorization").Single());

        var mail = new EmailChannel(new SmtpOptions { Host = "smtp.example.com", From = "raffaello@example.com", DefaultTo = "a@example.com;b@example.com" });
        Assert.True(mail.IsConfigured);
        using var msg = mail.Build(e with { Html = "<b>x</b>", Attachment = new byte[] { 1, 2 }, AttachmentName = "brief.pdf" }, "");
        Assert.Equal(2, msg.To.Count);
        Assert.True(msg.IsBodyHtml);
        Assert.Single(msg.Attachments);
        Assert.False(new EmailChannel(new SmtpOptions()).IsConfigured);
    }

    [Fact]
    public async Task Events_are_detected_from_the_data_and_the_brief_goes_out_with_a_pdf_by_email()
    {
        using var env = AssistantEnv.Create();
        var ev = NotifyEventDetector.Detect(env.Project.Snapshot, env.Project.Queue, Array.Empty<AssistantReminder>(), DateTime.Now.AddDays(-1), DateTime.Now);
        Assert.Contains(ev, e => e.Kind == NotifyEvents.OverCap && e.Title.Contains("P2-107"));
        Assert.Contains(ev, e => e.Kind == NotifyEvents.NewClaims && e.Title.StartsWith("ROOTS"));
        Assert.Contains(ev, e => e.Kind == NotifyEvents.InvoiceRejected);

        env.Store.Insert(new NotificationRule { Owner = "tester", EventKind = NotifyEvents.Brief, Channel = NotifyChannels.Email, MinSeverity = "OK", Address = "qs@example.com" });
        var mail = new Capture(NotifyChannels.Email);
        var hub = new NotificationHub(env.Store, () => new INotificationChannel[] { mail });
        var brief = hub.BuildBrief("tester", env.Project.Snapshot, env.Project.Queue, "en");
        var sent = await hub.SendBriefAsync(brief);
        Assert.Single(sent);
        Assert.NotNull(mail.Sent[0].E.Attachment);
        Assert.Contains("<html", mail.Sent[0].E.Html);
        Assert.Empty(await hub.SendBriefAsync(brief));                    // once a day
        Assert.Equal("Email is not configured.", (await hub.TestAsync("Email")).Replace("Email", "Email"));
    }
}

public class LocalizationTests
{
    [Fact]
    public void Every_string_has_an_arabic_translation_and_lookups_work()
    {
        var all = Loc.All();
        Assert.True(all.Count > 700, $"{all.Count} strings");
        Assert.All(all, r => Assert.False(string.IsNullOrWhiteSpace(r.Ar), r.Key));
        Assert.Equal("الإعدادات", Loc.Get("Nav_Settings", "ar"));
        Assert.Equal("SETTINGS", Loc.Get("Nav_Settings", "en"));
        Assert.Equal("Missing_Key_X", Loc.Get("Missing_Key_X", "ar"));
        Assert.Equal("حفظ المسودة", Loc.FromEnglish("save   draft", "ar"));
        Assert.Null(Loc.FromEnglish("SAVE DRAFT", "en"));
        Assert.Null(Loc.FromEnglish("no such text anywhere", "ar"));
        foreach (var k in new[] { "Nav_Dashboard", "Nav_Ledger", "Nav_Invoices", "Ask_Title", "Brief_Title", "Btn_Confirm", "Queue_Title", "Set_Language" })
            Assert.True(Loc.Has(k), k);
    }

    [Fact]
    public void Arabic_display_culture_is_gregorian_with_plain_separators_and_storage_stays_invariant()
    {
        var ar = Loc.Culture("ar");
        Assert.IsType<GregorianCalendar>(ar.DateTimeFormat.Calendar);
        Assert.Equal("1,234.50", 1234.5.ToString("N2", ar));
        Assert.Equal("-5", (-5).ToString(ar));
        Assert.Contains("2026", new DateTime(2026, 10, 2).ToString("dd MMM yyyy", ar));
        Assert.DoesNotContain("1448", new DateTime(2026, 10, 2).ToString("dd MMM yyyy", ar));
        Assert.Equal("ar-AE", Loc.WpfLanguageTag("ar"));
        Assert.Equal("en-GB", Loc.WpfLanguageTag("en"));
        Assert.Equal(1234.5, double.Parse("1234.5", CultureInfo.InvariantCulture));   // storage / parsing stays invariant
        Assert.Equal("ar", Loc.Normalize("ar-SA"));
        Assert.Equal("en", Loc.Normalize("fr"));
    }

    [Fact]
    public void House_rules_are_explained_in_both_languages()
    {
        Assert.All(HouseRuleCatalog.Rules, r => Assert.True(r.TextAr.Length > 20 && OfflineAssistant.IsArabic(r.TextAr), r.Code));
        Assert.Equal("HEIGHT_45", HouseRuleCatalog.Find("why is the 4.5 m height held").First().Code);
        Assert.Equal("STAGES", HouseRuleCatalog.Find("لماذا لا تجمع المراحل").First().Code);
    }
}

public class AssistantInfrastructureTests
{
    [Fact]
    public void Api_key_comes_from_the_vault_first_and_legacy_plain_keys_move_into_it()
    {
        var vault = new MemorySecretVault();
        var settings = new AppSettings { AnthropicApiKey = "sk-legacy-1234567890" };
        var (key, from) = ApiKeys.Resolve(vault, settings);
        if (Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") is not { Length: > 0 })
        {
            Assert.Equal("sk-legacy-1234567890", key);
            Assert.Equal(ApiKeys.Source.LegacySettings, from);
        }
        vault.Set(SecretNames.AnthropicKey, "sk-vault-abcdefghij");
        Assert.Equal((("sk-vault-abcdefghij", ApiKeys.Source.Vault)), ApiKeys.Resolve(vault, settings));
        Assert.False(ApiKeys.MigrateLegacy(vault, settings, () => { }));   // memory vault is not persistent: nothing moves
        Assert.Equal("sk-vaul...ghij", ApiKeys.Mask("sk-vault-abcdefghij"));
        vault.Set(SecretNames.AnthropicKey, "");
        Assert.Null(vault.Get(SecretNames.AnthropicKey));
        if (!OperatingSystem.IsWindows())
        {
            var dp = new DpapiSecretVault(Path.Combine(TestData.TempDir(), "secrets"));
            dp.Set("x", "secret");
            Assert.Equal("secret", dp.Get("x"));                          // session only, nothing on disk
            Assert.False(dp.IsPersistent);
        }
    }

    [Fact]
    public void A_key_typed_in_settings_goes_to_the_protected_store_and_never_into_settings_json()
    {
        var dir = TestData.TempDir();
        var path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path, "{\"UserName\":\"x\",\"AnthropicApiKey\":\"sk-old-plain\"}");
        var legacy = AppSettings.Load(path);
        Assert.Equal("sk-old-plain", legacy.StoredAnthropicApiKey);       // older files still read
        Assert.Equal("sk-old-plain", legacy.AnthropicApiKey);
        var vault = new Dictionary<string, string>();
        var s = new AppSettings();
        try
        {
            AppSettings.ProtectedApiKey = (() => vault.GetValueOrDefault("k"), v => vault["k"] = v);
            s.AnthropicApiKey = "sk-new-secret";
            Assert.Equal("sk-new-secret", vault["k"]);
            Assert.Equal("sk-new-secret", s.AnthropicApiKey);           // every module still reads it here
            s.Save(path);
            Assert.DoesNotContain("sk-new-secret", File.ReadAllText(path));
        }
        finally { AppSettings.ProtectedApiKey = null; }
    }

    [Fact]
    public void Settings_round_trip_without_secrets()
    {
        var path = Path.Combine(TestData.TempDir(), "assistant.json");
        var s = new AssistantSettings { Language = "ar", AllowReadProjectData = false, SmtpHost = "smtp.example.com" };
        s.Save(path);
        var back = AssistantSettings.Load(path);
        Assert.True(back.IsArabic);
        Assert.False(back.AllowReadProjectData);
        Assert.DoesNotContain("password", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
        Assert.False(new AssistantSettings().CloudDocumentReading);       // cloud reading is opt-in
        Assert.True(new AssistantSettings().AllowReadProjectData);
    }

    [Fact]
    public void Store_keeps_conversations_per_user_in_the_data_file_and_is_cleared_with_a_reset()
    {
        using var env = AssistantEnv.Create();
        var sel = new AssistantStoreSelector(() => env.Project.Store);
        var c = sel.Insert(new AssistantConversation { Owner = "tester", Title = "t", CreatedAt = DateTime.Now, LastAt = DateTime.Now });
        sel.Insert(new AssistantMessage { ConversationId = c.Id, Seq = 1, Role = "user", ContentJson = "[]", DisplayText = "hi" });
        sel.Insert(new AssistantConversation { Owner = "other", Title = "x" });
        Assert.Single(sel.Conversations("tester"));
        Assert.Single(sel.Messages(c.Id));
        Assert.Contains(env.Project.RecentActivity(10), a => a.TableName == "AssistantConversations");
        Assert.Contains(typeof(AssistantMessage), Raffaello.Core.Remote.ModuleEntities.All);
        env.Project.ResetData(seedDemo: false);
        Assert.Empty(sel.All<AssistantConversation>());
    }

    [Fact]
    public async Task Attachment_reader_reads_text_pdfs_excel_and_respects_cloud_reading()
    {
        var dir = TestData.TempDir();
        var xlsx = Path.Combine(dir, "boq.xlsx");
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("BOQ");
            ws.Cell(1, 1).Value = "ITEM"; ws.Cell(1, 2).Value = "QTY";
            ws.Cell(2, 1).Value = "Lighting point"; ws.Cell(2, 2).Value = 39;
            wb.SaveAs(xlsx);
        }
        var x = await new AttachmentReader(false).ReadAsync(xlsx);
        Assert.Equal("TEXT", x.ReadAs);
        Assert.Contains("Lighting point\t39", (string)x.Blocks[0]["text"]!);

        var pdf = Path.Combine(dir, "letter.pdf");
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
        Document.Create(d => d.Page(p => p.Content().Text("Delivery note 81064344 for PO RAF-P.O-E-045-2026, ten words and more here"))).GeneratePdf(pdf);
        var a = await new AttachmentReader(false).ReadAsync(pdf);
        Assert.Equal("TEXT", a.ReadAs);
        Assert.Equal(1, a.Pages);
        Assert.Contains("81064344", (string)a.Blocks[0]["text"]!);

        var png = Path.Combine(dir, "photo.png");
        await File.WriteAllBytesAsync(png, Raffaello.Core.Imaging.PngLite.Encode(new Raffaello.Core.Imaging.RgbaImage(4, 4), grey: false));
        var off = await new AttachmentReader(false).ReadAsync(png);
        Assert.Equal("NONE", off.ReadAs);
        Assert.Contains("cloud document reading is off", off.Note);
        var ocr = await new AttachmentReader(false, (b, m, ct) => Task.FromResult<string?>("DN 81064344")).ReadAsync(png);
        Assert.Equal("OCR", ocr.ReadAs);
        var cloud = await new AttachmentReader(true).ReadAsync(png);
        Assert.Equal("CLOUD", cloud.ReadAs);
        Assert.Equal("image", (string?)cloud.Blocks[0]["type"]);
        Assert.Equal("image/png", (string?)cloud.Blocks[0]["source"]!["media_type"]);
    }
}
