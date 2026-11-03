using BabyAI.Desktop;
using System.Text.Json.Nodes;

internal static class CandidateReaderTests
{
    private const string Sample = """
        {"mode":"ReadMatchingCandidates","process_id":123,"actions":[],"catalogue_changed":false,
         "row_identity_verified":false,"candidate_snapshot":{
          "PanelHandle":42,"IdentityVerified":false,"CanMatch":false,"SearchCompletionVerified":false,
          "Source":{"TableHandle":43,"ObservedRowPosition":1,"DocumentNumber":"1","Supplier":"Synthetic",
           "Title":"Perfume 100 ml","PriceText":"123,45","LinkedProductText":"<not specified>",
           "IdentityVerified":false,"CanMatch":false},
          "Candidates":[{"ObservedPosition":1,"Title":"Perfume 100 ml","DescriptionText":"Price: 0,00",
           "Selected":false,"IdentityVerified":false}]}}
        """;
    internal static void Run()
    {
        if (GingerCandidateReader.Validate(Sample) != 1) throw new Exception("Valid observation rejected");
        void Reject(Action<JsonNode> mutate)
        {
            var root = JsonNode.Parse(Sample)!;
            mutate(root);
            try { GingerCandidateReader.Validate(root.ToJsonString()); }
            catch (InvalidDataException) { return; }
            throw new Exception("Unsafe observation accepted");
        }
        Reject(r => r["mode"] = "OpenSelectedPrice");
        Reject(r => r["actions"]!.AsArray().Add("click"));
        Reject(r => r["process_id"] = 0);
        Reject(r => r["catalogue_changed"] = true);
        Reject(r => r["candidate_snapshot"]!["CanMatch"] = true);
        Reject(r => r["candidate_snapshot"]!["SearchCompletionVerified"] = true);
        Reject(r => r["candidate_snapshot"]!.AsObject().Remove("SearchCompletionVerified"));
        Reject(r => r["candidate_snapshot"]!["Source"]!["IdentityVerified"] = true);
        Reject(r => r["candidate_snapshot"]!["Source"]!["Title"] = "");
        Reject(r => r["candidate_snapshot"]!["Candidates"]![0]!["ObservedPosition"] = 9);
        Reject(r => r["candidate_snapshot"]!["Candidates"]![0]!["Selected"] = "false");
        Reject(r => r["candidate_snapshot"]!["Candidates"]![0]!["Title"] = new string('x', 513));
        Reject(r => r["candidate_snapshot"]!["Candidates"]![0]!.AsObject().Remove("IdentityVerified"));
        Reject(r => {
            var list = r["candidate_snapshot"]!["Candidates"]!.AsArray();
            var item = list[0]!.DeepClone();
            for (int i = 2; i <= 257; i++) { var next = item.DeepClone(); next["ObservedPosition"] = i; list.Add(next); }
        });
        var empty = JsonNode.Parse(Sample)!;
        empty["candidate_snapshot"]!["Candidates"]!.AsArray().Clear();
        if (GingerCandidateReader.Validate(empty.ToJsonString()) != 0) throw new Exception("Empty observation rejected");
        Console.WriteLine("PASS: candidate report contract rejects actions, authorization, malformed or oversized observations");
    }
}
