# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog 1.1.0](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.0.0-preview.1] - 2026-11-19

- Initial release of `PinkRooster.Extensions.DataIngestion.Chunkers` and `PinkRooster.Extensions.DataIngestion.CodeAnalysis`.
- Structure-aware recursive token chunking (`RecursiveTokenChunker`) with stepped bucket windowing and boundary snapping.
- Situational chunk contextualization (`ContextualChunkProcessor`) using `Microsoft.Extensions.AI` (`IChatClient`).
- Syntax-aware C# document parsing (`SourceCodeDocumentReader`) and AST-level chunking (`RoslynCodeChunker`) with symbol breadcrumbs.
- End-to-end samples application and BenchmarkDotNet throughput suite.
