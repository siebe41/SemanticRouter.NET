# SemanticRouter.Embeddings.Onnx

In-process sentence embeddings for [SemanticRouter.NET](https://github.com/siebe41/SemanticRouter.NET) with ONNX Runtime, so prompts are routed with no embedding server running. A small model (`all-MiniLM-L6-v2` by default, or `bge-small-en-v1.5`) is downloaded and checksum-verified on first use, or loaded from a directory of your own. ONNX Runtime's telemetry is turned off.

```bash
dotnet add package SemanticRouter.Embeddings.Onnx
```

```csharp
builder.Services
    .AddSemanticPromptRouting(builder.Configuration.GetSection("SemanticRouter"))
    .UseOnnxEmbeddings();   // or .AddOnnxEmbeddings() to let configuration or the dashboard choose
```

- [Configuration reference](https://github.com/siebe41/SemanticRouter.NET#configuration-reference) (`Embeddings:Model`, `ModelPath`, `Pooling`)
- [Network traffic](https://github.com/siebe41/SemanticRouter.NET/blob/main/docs/network.md) (the model download, and offline mode)
- [Changelog](https://github.com/siebe41/SemanticRouter.NET/blob/main/CHANGELOG.md)

MIT licence. The packages are versioned together and depend on each other exactly: update all the SemanticRouter packages an application uses at once.
