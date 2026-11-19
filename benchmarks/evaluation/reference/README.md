# Reference corpus review

These suites have been reviewed and frozen as baseline evaluation benchmarks for reporting retrieval lift.

- [.NET formatting prose questions](prose/questions.json) and [source manifest](prose/sources.json)
- [C# repository questions](csharp/questions.json) and [source manifest](csharp/sources.json)

Each question includes its verified answer and exact evidence lines in the local fixture. Every question ID and evidence line span was verified against local fixtures under `prose/fixtures/` and `csharp/fixtures/`. The review is frozen against corpus revision `6ec4099` and suite question IDs.

Each suite has an unlabelled distractor:
- The prose suite covers .NET numeric formatting with distractor `enum-format`.
- The C# suite covers selected timer and HTTP value-type behavior with distractor `runtime-time-provider`.

The [reference profile](profile.draft.json) pins comparison settings (`maxChunkTokens: 256`, `maxRetrievedTokens: 1280`, exact cosine similarity and term overlap ranking). The offline runner executes deterministic retrieval scoring in test runs and CLI benchmark runs.
