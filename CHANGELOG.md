# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog 1.1.0](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.0.0-preview.4] - 2026-11-20

### Added
- `TextDocumentReader` and `TextDocumentReaderOptions` in `PinkRooster.Extensions.DataIngestion.Chunkers` for streaming verbatim document slices with 1-based start line metadata and Markdown heading extraction outside code fences.
- Line location tracking (`file_path`, `start_line`, `end_line`, `line_range`) in `RecursiveTokenChunker` across prose, overlap, code fences, and tables when document elements carry source lines.

## [1.0.0-preview.3] - 2026-11-20

### Added
- Location metadata keys (`file_path`, `start_line`, `end_line`, `line_range`, `start_column`, `end_column`) on `ChunkMetadataKeys`.
- 1-based source line span and column extraction in `RoslynCodeChunker` using Roslyn AST `SyntaxTree.GetLineSpan`.
- Document-relative line offset tracking in `CSharpSourceBlocks` for fenced C# blocks embedded within Markdown documents.
- Disk-aligned source coordinate mapping in `DirectiveContext` across stripped `#region` directives.

## [1.0.0-preview.2] - 2026-11-20

### Changed
- Consolidated table row-window chunking into `TableSplitter`, enabling oversized row slicing with repeated headers for document tables.
- Decoupled CRLF-aware line splitting and abbreviation protection in `ProseSplitter` so custom separator lists retain line-ending and abbreviation integrity.
- Centralized table row range formatting and parsing in `TableRowRange`.
- Extracted `TableChunk` into its own file.
- Standardized private static field names and extracted named heuristic constants.

## [1.0.0-preview.1] - 2026-11-19

- Initial release of `PinkRooster.Extensions.DataIngestion.Chunkers` and `PinkRooster.Extensions.DataIngestion.CodeAnalysis`.
- Structure-aware recursive token chunking (`RecursiveTokenChunker`) with stepped bucket windowing and boundary snapping.
- Situational chunk contextualization (`ContextualChunkProcessor`) using `Microsoft.Extensions.AI` (`IChatClient`).
- Syntax-aware C# document parsing (`SourceCodeDocumentReader`) and AST-level chunking (`RoslynCodeChunker`) with symbol breadcrumbs.
- End-to-end samples application and BenchmarkDotNet throughput suite.
