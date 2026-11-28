using BabyAI.Desktop;
using System.Text.Json;
var root = Path.Combine(Path.GetTempPath(), "babyai-agent-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
void Check(bool ok, string label) { if (!ok) throw new Exception(label); Console.WriteLine("PASS: " + label); }
var store = new AgentProfileStore(Path.Combine(root, "agents.json"));
var catalog = store.Load();
catalog.Agents.Add(new("custom", "Analyst", "Check reports", "mint", -100, 20));
store.Save(catalog);
Check(store.Load().Agents.Single(a => a.Id == "custom").X == -100, "profiles and multi-monitor position round trip");
var original = File.ReadAllText(Path.Combine(root,"agents.json"));
try { store.Save(catalog with { Agents = [AgentProfileStore.Ginger, AgentProfileStore.Ginger] }); throw new Exception("invalid accepted"); } catch (InvalidDataException) { }
Check(File.ReadAllText(Path.Combine(root,"agents.json")) == original, "invalid profile preserves persisted data");
var learning = new GingerLearning(Path.Combine(root, "learning"));
learning.SaveLanguageLesson(new("supplier", "Моё объяснение", "Понял объяснение", true, DateTimeOffset.UtcNow));
var lessonReloaded = new GingerLearning(Path.Combine(root, "learning"));
Check(lessonReloaded.ReadLanguageLesson("supplier")?.Enabled == true, "language lesson survives restart");
Check(lessonReloaded.ReadLanguageLesson("other") is null, "language lesson supplier scope");
Check(lessonReloaded.AutomaticCandidate("supplier", "different source", ["candidate"]) is null, "language lesson alone cannot authorize matching");
learning.SaveLanguageLesson(lessonReloaded.ReadLanguageLesson("supplier")! with { Enabled = false });
Check(lessonReloaded.ReadLanguageLesson("supplier")?.Enabled == false, "language lesson revocation survives restart");
learning.SaveLanguageLesson(new("lesson-supplier", "Правило", "Понимание", true, DateTimeOffset.UtcNow));
Check(learning.LessonForReview("lesson-supplier", "new source") is not null, "active lesson available for review on new source");
Check(learning.LessonForReview("other-supplier", "new source") is null, "review cannot borrow another supplier lesson");
learning.Teach(new("lesson-supplier", "blocked source", "candidate", "отзыв", false, DateTimeOffset.UtcNow));
Check(learning.LessonForReview("lesson-supplier", "blocked source") is null, "model review cannot bypass exact pair revocation");
learning.SaveLanguageLesson(learning.ReadLanguageLesson("lesson-supplier")! with { Enabled = false });
Check(learning.LessonForReview("lesson-supplier", "new source") is null, "revoked lesson unavailable to automatic pass review");
var multiple = new GingerLearning(Path.Combine(root, "multiple-lessons"));
var firstLesson = new GingerLearning.LanguageLesson("Альянс", "Немного подмят означает тестер.",
    "Обозначение поставщика относится к тестеру.", true, DateTimeOffset.UtcNow, Guid.NewGuid().ToString("N"));
var secondLesson = new GingerLearning.LanguageLesson("Альянс", "Название аромата в нашем каталоге записано заглавными буквами.",
    "Регистр названия не означает другой аромат.", true, DateTimeOffset.UtcNow, Guid.NewGuid().ToString("N"));
multiple.SaveLanguageLessonIfUnchanged(firstLesson, Array.Empty<GingerLearning.LanguageLesson>());
var firstBundle = multiple.ReadLanguageLessons("Альянс");
Check(GingerLearning.ComposeLanguageLessons(firstBundle) == firstLesson.Text, "single lesson keeps exact original text");
multiple.SaveLanguageLessonIfUnchanged(secondLesson, firstBundle);
var bothLessons = multiple.ReadLanguageLessons("Альянс");
Check(bothLessons.SequenceEqual(new[] { firstLesson, secondLesson }), "independent lessons accumulate in publication order");
var combined = GingerLearning.ComposeLanguageLessons(bothLessons);
Check(combined == "1. " + firstLesson.Text + "\n\n2. " + secondLesson.Text,
    "active lesson context includes both explanations without replacing the first");
Check(multiple.LessonForReview("Альянс", "Новая позиция")?.Text == combined,
    "review receives complete supplier lesson context");
Check(multiple.ReadLanguageLessons("Другой поставщик").Count == 0
    && multiple.LessonForReview("Другой поставщик", "Новая позиция") is null,
    "lesson bundle never crosses supplier");
multiple.Teach(new("Альянс", "Запрещённая пара", "Кандидат", "Запрет пользователя", false, DateTimeOffset.UtcNow));
Check(multiple.LessonForReview("Альянс", "Запрещённая пара") is null,
    "combined lesson context cannot bypass explicit pair veto");
var revokedFirst = firstLesson with { Enabled = false, RecordedAt = DateTimeOffset.UtcNow };
multiple.SaveLanguageLessonIfUnchanged(revokedFirst, bothLessons);
var afterRevoke = multiple.ReadLanguageLessons("Альянс");
Check(afterRevoke.SequenceEqual(new[] { revokedFirst, secondLesson })
    && GingerLearning.ComposeLanguageLessons(afterRevoke) == secondLesson.Text,
    "one lesson revoked independently while another remains active and order remains stable");
Check(multiple.LessonForReview("Альянс", "Новая позиция")?.Text == secondLesson.Text,
    "revoked lesson excluded from review context");
try
{
    multiple.SaveLanguageLessonIfUnchanged(secondLesson with { Text = "Изменение из устаревшего окна" }, bothLessons);
    throw new Exception("stale supplier bundle accepted");
}
catch (InvalidOperationException) { }
Check(multiple.ReadLanguageLessons("Альянс").SequenceEqual(afterRevoke),
    "change to another lesson invalidates the entire stale editor bundle without data loss");
try
{
    multiple.SaveLanguageLessonIfUnchanged(secondLesson with { LessonId = Guid.NewGuid().ToString("N"), Text = " " + secondLesson.Text + " " }, afterRevoke);
    throw new Exception("duplicate active lesson accepted");
}
catch (InvalidDataException) { }
Check(multiple.ReadLanguageLessons("Альянс").SequenceEqual(afterRevoke),
    "duplicate active text cannot grow memory under a new identity");
var otherSupplier = secondLesson with { Supplier = "Другой поставщик", LessonId = Guid.NewGuid().ToString("N") };
multiple.SaveLanguageLessonIfUnchanged(otherSupplier, Array.Empty<GingerLearning.LanguageLesson>());
Check(multiple.ReadLanguageLessons("Другой поставщик").Single() == otherSupplier,
    "same explanation may be explicitly taught to another supplier");
try
{
    GingerLearning.ComposeLanguageLessons(new[] { secondLesson, otherSupplier });
    throw new Exception("mixed supplier context accepted");
}
catch (InvalidDataException) { }
Check(new GingerLearning(Path.Combine(root, "multiple-lessons")).ReadLanguageLessons("Альянс").SequenceEqual(afterRevoke),
    "all lesson identities and independent revocation survive restart");

var legacyDirectory = Path.Combine(root, "legacy-lessons");
var legacyFolder = Path.Combine(legacyDirectory, "language-lessons");
Directory.CreateDirectory(legacyFolder);
// These are the old on-disk shape, deliberately without a LessonId field.
File.WriteAllText(Path.Combine(legacyFolder, "0001.json"), JsonSerializer.Serialize(new
    { Supplier = "Старый поставщик", Text = "Устаревшее объяснение", Understanding = "Первое понимание", Enabled = true, RecordedAt = DateTimeOffset.UtcNow }));
File.WriteAllText(Path.Combine(legacyFolder, "0002.json"), JsonSerializer.Serialize(new
    { Supplier = "Старый поставщик", Text = "Последнее старое объяснение", Understanding = "Исправленное понимание", Enabled = false, RecordedAt = DateTimeOffset.UtcNow }));
var legacyLearning = new GingerLearning(legacyDirectory);
var legacyBundle = legacyLearning.ReadLanguageLessons("Старый поставщик");
Check(legacyBundle.Count == 1 && legacyBundle[0].LessonId == "legacy" && !legacyBundle[0].Enabled
    && legacyBundle[0].Text == "Последнее старое объяснение" && GingerLearning.ComposeLanguageLessons(legacyBundle) == "",
    "old history migrates as one latest lesson and never reactivates superseded text");
var additionalLegacyLesson = firstLesson with { Supplier = "Старый поставщик", LessonId = Guid.NewGuid().ToString("N") };
legacyLearning.SaveLanguageLessonIfUnchanged(additionalLegacyLesson, legacyBundle);
Check(legacyLearning.ReadLanguageLessons("Старый поставщик").Count == 2
    && legacyLearning.LessonForReview("Старый поставщик", "Новая строка")?.Text == additionalLegacyLesson.Text,
    "new separate lesson coexists with revoked legacy history without reviving it");

var bounded = new GingerLearning(Path.Combine(root, "bounded-lessons"));
var longLesson = firstLesson with { Text = new string('а', 4000), LessonId = Guid.NewGuid().ToString("N") };
bounded.SaveLanguageLessonIfUnchanged(longLesson, Array.Empty<GingerLearning.LanguageLesson>());
var boundedBefore = bounded.ReadLanguageLessons("Альянс");
var boundedFiles = Directory.GetFiles(Path.Combine(root, "bounded-lessons", "language-lessons"), "*.json");
try
{
    bounded.SaveLanguageLessonIfUnchanged(secondLesson, boundedBefore);
    throw new Exception("oversized combined context accepted");
}
catch (InvalidDataException ex) { Check(ex.Message.Contains("4000"), "context limit provides actionable explanation"); }
Check(bounded.ReadLanguageLessons("Альянс").SequenceEqual(boundedBefore)
    && Directory.GetFiles(Path.Combine(root, "bounded-lessons", "language-lessons"), "*.json").SequenceEqual(boundedFiles)
    && GingerLearning.ComposeLanguageLessons(boundedBefore).Length == 4000,
    "context overflow preserves every published record and never truncates existing explanation");
bounded.SaveLanguageLessonIfUnchanged(longLesson with { Enabled = false }, boundedBefore);
bounded.SaveLanguageLessonIfUnchanged(secondLesson, bounded.ReadLanguageLessons("Альянс"));
Check(GingerLearning.ComposeLanguageLessons(bounded.ReadLanguageLessons("Альянс")) == secondLesson.Text,
    "disabled lesson does not consume active context budget");
var volumeFirst = "Acme Bloom (муж) 30ml EDP";
var concentrationFirst = "Acme Bloom (муж) edp 30 мл";
Check(learning.AutomaticCandidate("supplier", volumeFirst, [concentrationFirst]) == concentrationFirst,
    "single adjacent volume concentration order normalized");
foreach (var candidate in new[] { "Acme Bloom (муж) edt 30ml", "Acme Bloom (муж) edp 50ml",
    "Acme Bloom (жен) edp 30ml", concentrationFirst + " tester", concentrationFirst + " vintage",
    concentrationFirst + " Travel Spray", "Bloom Acme (муж) edp 30ml" })
    Check(learning.AutomaticCandidate("supplier", volumeFirst, [candidate]) is null, "attribute reordering preserves identity: " + candidate);
Check(learning.AutomaticCandidate("supplier", "Acme Bloom 30ml edp + 50ml edt", ["Acme Bloom edp 30ml + edt 50ml"]) is null,
    "multi component set is not reordered");
Check(learning.AutomaticCandidate("supplier", "Acme Bloom 30ml Travel Spray edp", ["Acme Bloom edp 30ml Travel Spray"]) is null,
    "attributes never move across intervening words");
Check(learning.AutomaticCandidate("supplier", volumeFirst, [concentrationFirst, volumeFirst]) is null,
    "equivalent spellings remain duplicate candidates");
var travelReview = learning.AssessAutomatic("Альянс", "Mont Blanc Explorer (M) 30ml EDP Travel Spray",
    ["Mont Blanc EXPLORER (муж) edp 30ml ", "Mont Blanc EXPLORER (муж) edp 30ml (тестер)"]);
Check(travelReview.Decision is null && travelReview.Reason.Contains("travel spray") && travelReview.Reason.Contains("муж"),
    "real Mont Blanc ambiguity retains distinguishing words and explains review");
Check(learning.AssessAutomatic("supplier", "Acme edt 30ml", []).Reason == "Список кандидатов пуст.", "empty candidate list explained");
var moschino = "MOSCHINO CHEAPANDCHIC LIGHT CLOUDS edt 50 ml";
var moschinoExpanded = "Moschino CHEAP AND CHIC LIGHT CLOUDS тв 50ml";
Check(learning.AutomaticCandidate("supplier", moschino, [moschinoExpanded]) == moschinoExpanded, "Moschino joined line spelling accepted");
foreach (var variant in new[] { moschinoExpanded + " tester", moschinoExpanded + " vintage", moschinoExpanded + " lux", moschinoExpanded.Replace("50ml", "100ml"), moschinoExpanded.Replace("тв", "пв"), moschinoExpanded.Replace("CLOUDS", "CLOUDS Intense"), moschinoExpanded.Replace("тв", "(жен) тв") })
    Check(learning.AutomaticCandidate("supplier", moschino, [variant]) is null, "Moschino spelling preserves distinguishing tokens: " + variant);
Check(learning.AutomaticCandidate("supplier", moschino, [moschinoExpanded, moschinoExpanded]) is null, "Moschino spelling rejects duplicate matches");
Check(learning.AutomaticCandidate("supplier", moschino.Replace("MOSCHINO", "Other"), [moschinoExpanded.Replace("Moschino", "Other")]) is null, "Joined spelling does not generalize to other brands");
Check(learning.AutomaticCandidate("supplier", "Acme Bloom edt 100 ml tester", ["Acme Bloom тв 100ml (тестер)"]) is not null, "exact semantics accepts concentration and unit aliases");
foreach(var title in new[] { "Acme Bloom edt 50ml tester", "Acme Bloom Intense edt 100ml tester", "Acme Bloom edt 100ml", "Acme Bloom edp 100ml tester" })
 Check(learning.AutomaticCandidate("supplier", "Acme Bloom edt 100 ml tester", [title]) is null, "different variant rejected: " + title);
Check(learning.AutomaticCandidate("supplier", "Acme Bloom edt 100 ml tester", ["Acme Bloom edt 100 ml tester", "Acme Bloom edt 100 ml tester"]) is null, "duplicate candidates rejected");
learning.Teach(new("supplier", "Supplier special title", "Exact product", "user explanation", true, DateTimeOffset.UtcNow));
Check(learning.AutomaticCandidate("supplier", "Supplier special title", ["Exact product"]) == "Exact product", "explicit scoped teaching used");
Check(learning.AutomaticCandidate("other supplier", "Supplier special title", ["Exact product"]) is null, "learning never crosses supplier");
Check(learning.AutomaticCandidate("supplier", "Changed special title", ["Exact product"]) is null, "renamed source requires review");
learning.Teach(new("supplier", "Supplier special title", "Exact product", "revoke auto", false, DateTimeOffset.UtcNow));
Check(learning.AutomaticCandidate("supplier", "Supplier special title", ["Exact product"]) is null, "later user correction revokes automatic pairing");
Check(learning.AssessAutomatic("supplier", "Supplier special title", ["Exact product"]).Reason.Contains("запрещает"), "revocation reason distinguished from name mismatch");
learning.TeachDentedTesterRule("supplier", true);
var reloaded = new GingerLearning(Path.Combine(root, "learning"));
var dented = "Acme Bloom edt 100 ml (немного подмят)";
var tester = "Acme Bloom edt 100 ml tester";
Check(reloaded.AutomaticCandidate("supplier", dented, [tester]) == tester, "condition rule persists and applies to a new source title");
Check(reloaded.DecideAutomatic("supplier", dented, [tester])?.Basis == "dented_tester_rule", "decision records applied supplier condition");
Check(reloaded.DecideAutomatic("supplier", tester, [tester])?.Basis == "exact_identity", "enabled but unused rule is not claimed as decision basis");
reloaded.Teach(new("supplier", "Learned source", tester, "Explicit user explanation", true, DateTimeOffset.UtcNow));
var explained = reloaded.DecideAutomatic("supplier", "Learned source", [tester]);
Check(explained?.Basis == "learned_pair" && explained.Explanation.Contains("Explicit user explanation"), "learned decision retains user explanation");
Check(reloaded.AutomaticCandidate("other supplier", dented, [tester]) is null, "condition rule cannot cross supplier");
Check(reloaded.AutomaticCandidate("supplier", dented, ["Acme Bloom edt 50 ml tester"]) is null, "condition rule preserves volume");
Check(reloaded.AutomaticCandidate("supplier", "Acme Bloom edt 100 ml (недолив)", [tester]) is null, "condition rule does not generalize damage descriptions");
foreach (var variant in new[] { "Acme Bloom edp 100 ml tester", "Acme Bloom edt 100 ml tester vintage", "Acme Bloom edt 100 ml tester lux", "Acme Bloom Intense edt 100 ml tester", "Acme Bloom edt 100 ml tester women" })
    Check(reloaded.AutomaticCandidate("supplier", dented, [variant]) is null, "learned condition preserves product identity: " + variant);
Check(reloaded.AutomaticCandidate("supplier", dented, [tester, tester]) is null, "condition rule rejects duplicate candidates");
var correctedSource = "Acme Other edt 100 ml (немного подмят)";
reloaded.Teach(new("supplier", correctedSource, "Acme Other edt 100 ml tester", "User revoked this specific exception", false, DateTimeOffset.UtcNow));
Check(reloaded.AutomaticCandidate("supplier", correctedSource, ["Acme Other edt 100 ml tester"]) is null, "exact user correction overrides enabled general condition");
var ruleFolder = Path.Combine(root, "learning", "condition-rules");
var historyBeforeRevoke = Directory.GetFiles(ruleFolder, "*.json").Length;
reloaded.TeachDentedTesterRule("supplier", false);
Check(reloaded.AutomaticCandidate("supplier", dented, [tester]) is null, "revoked condition rule no longer authorizes matching");
Check(Directory.GetFiles(ruleFolder, "*.json").Length == historyBeforeRevoke + 1, "revocation preserves learning history");
var damagedRule = Path.Combine(ruleFolder, "zz-invalid.json");
File.WriteAllText(damagedRule, "{invalid");
var rejectedDamagedHistory = false;
try { reloaded.AutomaticCandidate("supplier", dented, [tester]); }
catch (JsonException) { rejectedDamagedHistory = true; }
Check(rejectedDamagedHistory, "damaged rule history blocks automatic matching");
var claims = Path.Combine(root, "claims");
Directory.CreateDirectory(claims);
var claimPath = Path.Combine(claims, "reserved.json");
File.WriteAllText(claimPath, JsonSerializer.Serialize(new { identity = JsonSerializer.Serialize(new object[] { 10, 20, "doc", "supplier", 4, "source" }), status = "reserved_no_retry" }));
Check(GingerClaims.HasReservation(claims, 10, 20, "doc", "supplier", 4, "source"), "previous attempted source identified without retry");
Check(!GingerClaims.HasReservation(claims, 10, 20, "doc", "supplier", 5, "next source"), "unrelated next row may proceed");
Check(!GingerClaims.HasReservation(claims, 10, 20, "other doc", "supplier", 4, "source"), "reservation stays scoped to document");
Check(File.Exists(claimPath), "preflight preserves reservation");
File.WriteAllText(claimPath, "{invalid");
var malformedClaimBlocked = false;
try { GingerClaims.HasReservation(claims, 10, 20, "doc", "supplier", 4, "source"); }
catch (JsonException) { malformedClaimBlocked = true; }
Check(malformedClaimBlocked, "corrupt reservation blocks processing");
Directory.Delete(root, true);
