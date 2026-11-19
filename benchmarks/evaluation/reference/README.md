# Reference corpus review

These draft suites await human review before they can be frozen or used to report lift.

- [.NET formatting prose questions](prose/questions.json) and [source manifest](prose/sources.json)
- [C# repository questions](csharp/questions.json) and [source manifest](csharp/sources.json)

Each question includes its proposed answer and exact evidence lines in the local fixture. Review whether the evidence supports the answer, whether the question is ambiguous, and whether other documents offer equally relevant evidence. Record the review against the reviewed corpus revision and question IDs before scoring.

Each suite has an unlabelled distractor. The prose suite covers .NET numeric formatting. The C# suite covers selected timer and HTTP value-type behavior. Results from these suites apply to those cases.

The [draft reference profile](profile.draft.json) pins the local runtime, model digests, and comparison settings. Verify the version and model digests using Ollama's `/api/version` and `/api/tags` endpoints before collecting headers or ranking documents. Keep the endpoint bound to loopback and disable cloud mode. The comparison tokenizer budgets the embedding input, including its search prefix. Ollama also checks the embedding model's own context limit with truncation disabled.
