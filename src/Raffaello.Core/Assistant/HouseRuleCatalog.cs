namespace Raffaello.Core.Assistant;

public sealed record HouseRule(string Code, string Title, string Text, string TextAr, string[] Keywords);

/// <summary>The house rules the assistant explains (explain_rule, and the offline answers). English + Arabic.</summary>
public static class HouseRuleCatalog
{
    public static readonly HouseRule[] Rules =
    {
        new("STAGES", "Stages are never summed",
            "1ST FIX, 2ND FIX and FINAL FIX measure the same points. Totals are always shown per stage; adding stages together would count every point two or three times.",
            "المراحل (التمديد الأول والثاني والنهائي) تقيس نفس النقاط، لذلك تُعرض المجاميع لكل مرحلة على حدة ولا تُجمع المراحل مع بعضها أبداً.",
            new[] { "stage", "stages", "sum", "1st", "2nd", "final", "مرحلة", "مراحل" }),
        new("OVER", "OVER - claim above the cap",
            "A claim above the remaining quantity (PROJECT QTY minus the claims of all subcontractors for the same room x stage x item) is blocked unless a reason is given; with a reason it is posted at full quantity and flagged OVER.",
            "المطالبة التي تتجاوز الكمية المتبقية (كمية المشروع ناقص مطالبات جميع المقاولين لنفس الغرفة والمرحلة والبند) تُرفض إلا مع ذكر السبب، ومع السبب تُسجّل بالكمية كاملة وتُعلَّم OVER.",
            new[] { "over", "cap", "exceed", "above", "تجاوز", "زيادة" }),
        new("CHECK", "CHECK - claimed above the approved WIR",
            "When the cumulative claim is above the quantity done on site with an approved WIR, the line is CHECK: hold the difference and certify only the WIR quantity.",
            "عندما تكون المطالبة التراكمية أكبر من الكمية المنفذة بطلب فحص (WIR) معتمد، يُعلَّم البند CHECK: يُحجز الفرق ولا يُعتمد إلا كمية الـ WIR.",
            new[] { "check", "wir", "hold", "certify", "فحص", "حجز" }),
        new("EMT", "EMT is rework",
            "EMT lines are rework: they are recorded but never count as progress and never against the PROJECT QTY cap.",
            "بنود EMT هي إعادة عمل: تُسجّل لكنها لا تُحتسب كتقدم ولا تُخصم من سقف كمية المشروع.",
            new[] { "emt", "rework", "إعادة" }),
        new("CAP", "PROJECT QTY caps the claims",
            "Remaining = PROJECT QTY - claims of every subcontractor for the same room x stage x item. The plan quantity counts, not the length-revised quantity.",
            "المتبقي = كمية المشروع ناقص مطالبات كل المقاولين لنفس الغرفة والمرحلة والبند. تُحتسب كمية المخطط وليس الكمية المعدلة بقاعدة الطول.",
            new[] { "project qty", "remaining", "cap", "balance", "متبقي", "المتبقي", "كمية المشروع" }),
        new("SITE_WIR", "SITE % for progress, WIR % for invoices",
            "Progress is compared after SITE %; invoices are valued on WIR %. Invoice quantity = base quantity x SITE % x WIR %.",
            "يُقارن التقدم بعد تطبيق نسبة الموقع (SITE %)، وتُقيَّم الفواتير على نسبة الفحص (WIR %). كمية الفاتورة = الكمية الأساسية × نسبة الموقع × نسبة الفحص.",
            new[] { "site %", "site", "wir %", "percent", "نسبة" }),
        new("LENGTH_15", "15 m route length rule",
            "On 2nd-fix pulling items a point longer than 15 m counts proportionally with a minimum of one: qty per point = max(1, L / 15), e.g. 20 m = 1.3. Only the revised quantity is invoiced; caps use the plan quantity; pending checks are held out of the invoice.",
            "في بنود سحب الكابلات (التمديد الثاني) تُحتسب النقطة الأطول من 15 م بشكل نسبي وبحد أدنى نقطة واحدة: الكمية لكل نقطة = الأكبر من (1، الطول ÷ 15)، مثلاً 20 م = 1.3. تُفوتر الكمية المعدلة فقط، والسقف يُحسب على كمية المخطط، والفحوصات المعلقة تُستبعد من الفاتورة.",
            new[] { "15", "length", "route", "pulling", "data rack", "طول", "15 م" }),
        new("HEIGHT_45", "Above 4.5 m height check",
            "Work claimed above 4.5 m is priced at the higher contract item only for the quantity accepted after checking (PENDING / ACCEPTED / PARTLY / REJECTED); the rest is priced at the normal item. Pending lines are held out of the invoice.",
            "الأعمال المطالب بها فوق 4.5 م تُسعّر بالبند الأعلى فقط للكمية المقبولة بعد الفحص (معلق / مقبول / جزئي / مرفوض)، والباقي بالبند العادي. البنود المعلقة تُستبعد من الفاتورة.",
            new[] { "4.5", "height", "high", "ارتفاع", "4.5 م" }),
        new("CUMULATIVE", "Cumulative invoices",
            "A cumulative invoice (INV 1-N) replaces that subcontractor's earlier lines for the same key until his per-invoice files are imported and split: current(n) = cum(n) - cum(n-1), allocated to rooms by PROJECT QTY share.",
            "الفاتورة التراكمية (1 إلى N) تحل محل بنود المقاول السابقة لنفس المفتاح حتى تُستورد ملفات فواتيره وتُقسَّم: الحالي(n) = التراكمي(n) - التراكمي(n-1)، ويُوزع على الغرف حسب حصة كمية المشروع.",
            new[] { "cumulative", "split", "تراكمي", "تراكمية" }),
        new("DATA_RACK", "DATA RACK = extra data points (15 m rule)",
            "DATA RACK in the tracker ledger is not a rack: it is extra data points claimed because of long routes. It becomes a length claim on DATA 2ND FIX with plan qty 0, so it never counts against PROJECT QTY.",
            "بند DATA RACK في سجل المتابعة ليس خزانة: هو نقاط بيانات إضافية بسبب طول المسارات، ويُسجّل كمطالبة طول على DATA التمديد الثاني بكمية مخطط صفر فلا يُخصم من كمية المشروع.",
            new[] { "data rack", "rack" }),
        new("BOQ_CODE", "BOQ code choice",
            "POWER -> small power points; LIGHT -> lighting points to the room's area type (apartment / BOH / FOH / balcony, default apartment); switches -> switch rows; emergency -> emergency lighting; GAS -> gas meter items; DATA / GRMS 1st fix -> the WALL outlet items. Manual overrides are learned.",
            "اختيار بند جدول الكميات: الباور -> نقاط القوى الصغيرة، الإنارة -> نقاط الإنارة حسب نوع المنطقة (شقة / خدمات / واجهة / بلكونة، الافتراضي شقة)، المفاتيح -> بنود المفاتيح، الطوارئ -> إنارة الطوارئ، الغاز -> بنود عدادات الغاز، البيانات وGRMS تمديد أول -> بنود المخارج الجدارية.",
            new[] { "boq", "code", "mapping", "lighting", "بند", "جدول الكميات" }),
        new("REVISIONS", "Invoice revisions and Aconex",
            "Each invoice has revisions (Rev 0, Rev 1 ...). A rejection records the reason; the next revision is built from the ledger; approved revisions are locked and become 'previous' for the next invoice. Each revision keeps its Aconex workflow no.",
            "لكل فاتورة مراجعات (0، 1 ...). عند الرفض يُسجّل السبب وتُبنى المراجعة التالية من السجل، والمراجعات المعتمدة تُقفل وتصبح 'السابق' للفاتورة التالية، ولكل مراجعة رقم سير عمل Aconex خاص بها.",
            new[] { "revision", "rejected", "reject", "aconex", "approve", "مراجعة", "رفض", "اعتماد" }),
        new("DN_LOCK", "A DN line is invoiced once",
            "Each delivery-note line can be on one supplier invoice only; delivered above the PO quantity is an OVER alarm; DN quantities in KM are converted to M (x1000); pipes PCS <-> M at 6 m per piece unless the PO line says otherwise.",
            "كل بند في إشعار التسليم يُفوتر مرة واحدة فقط، والتسليم فوق كمية أمر الشراء إنذار OVER، وكميات الكيلومتر تُحول إلى متر (×1000)، والمواسير قطعة <-> متر بطول 6 م ما لم يذكر أمر الشراء غير ذلك.",
            new[] { "dn", "delivery", "po", "mir", "pipe", "pcs", "إشعار", "تسليم", "أمر شراء" }),
        new("POINTS", "Point counting",
            "Twin socket = 1 point; twin data = 2 points at 2ND FIX; TV + soundbar = 1; switches under LIGHT; thermostat CP-4 counted in GRMS only; ceiling WAP = flexible.",
            "المقبس المزدوج = نقطة واحدة، مخرج البيانات المزدوج = نقطتان في التمديد الثاني، التلفاز + مكبر الصوت = نقطة واحدة، المفاتيح ضمن الإنارة، الثرموستات CP-4 يُحتسب في GRMS فقط.",
            new[] { "twin", "socket", "point", "switch", "thermostat", "نقطة", "مقبس" }),
    };

    /// <summary>Rules matching a topic (catalog first, then the rules engine's own descriptions).</summary>
    public static IEnumerable<HouseRule> Find(string topic, IEnumerable<(string Code, string Title, string Description)>? engineRules = null)
    {
        var t = (topic ?? "").Trim().ToLowerInvariant();
        if (t.Length == 0) yield break;
        var scored = Rules.Select(r => (r, score: r.Keywords.Count(k => t.Contains(k, StringComparison.OrdinalIgnoreCase)) * 2
                                                    + (r.Code.Equals(t, StringComparison.OrdinalIgnoreCase) ? 5 : 0)
                                                    + (r.Title.Contains(t, StringComparison.OrdinalIgnoreCase) ? 2 : 0)))
            .Where(x => x.score > 0).OrderByDescending(x => x.score).Select(x => x.r).ToList();
        foreach (var r in scored) yield return r;
        if (engineRules is null) yield break;
        foreach (var e in engineRules)
            if (t.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(w => w.Length > 2 && (e.Title + " " + e.Description + " " + e.Code).Contains(w, StringComparison.OrdinalIgnoreCase)))
                yield return new HouseRule(e.Code, e.Title, e.Description, e.Description, Array.Empty<string>());
    }
}
