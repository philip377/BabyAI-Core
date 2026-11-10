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
