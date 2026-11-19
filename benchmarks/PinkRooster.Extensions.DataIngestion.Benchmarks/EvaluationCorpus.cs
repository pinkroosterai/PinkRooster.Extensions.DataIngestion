namespace PinkRooster.Extensions.DataIngestion.Benchmarks;

internal sealed record EvaluationCorpus(
    IReadOnlyList<EvaluationSource> Sources,
    IReadOnlyList<EvaluationQuestion> Questions,
    string Fingerprint);

internal sealed record EvaluationSource(string Id, string Path, string Text, string Digest);

internal sealed record EvaluationQuestion(string Id, string Prompt, IReadOnlyList<EvidenceSpan> Evidence);

internal sealed record EvidenceSpan(string SourceId, int Offset, int Length, int StartLine, int EndLine);
