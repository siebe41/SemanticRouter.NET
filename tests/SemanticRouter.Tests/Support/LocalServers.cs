namespace SemanticRouter.Tests.Support;

/// <summary>Recorded shapes of LM Studio's and Ollama's model APIs, as stub handlers.</summary>
internal static class LocalServers
{
    /// <summary>
    /// LM Studio with qwen3-8b loaded (32K context) and a 30B coder, a vision model and an embedding model downloaded but
    /// not loaded. With just-in-time loading on, <c>/v1/models</c> lists every downloaded model; with it off, only
    /// the loaded one.
    /// </summary>
    public static StubHttpMessageHandler LmStudio(bool justInTime, string chatId = "qwen/qwen3-8b", string coderId = "qwen/qwen3-coder-30b") =>
        StubHttpMessageHandler.PathsOnPort(
            1234,
            ("/v1/models", _ => justInTime
                ? $$"""{"object":"list","data":[{"id":"{{chatId}}","object":"model"},{"id":"{{coderId}}","object":"model"},{"id":"google/gemma-3-12b","object":"model"},{"id":"text-embedding-nomic-embed-text-v1.5","object":"model"}]}"""
                : $$"""{"object":"list","data":[{"id":"{{chatId}}","object":"model"}]}"""),
            ("/api/v0/models", _ => $$"""
                {"object":"list","data":[
                  {"id":"{{chatId}}","object":"model","type":"llm","publisher":"qwen","arch":"qwen3","quantization":"Q4_K_M","state":"loaded","max_context_length":40960,"loaded_context_length":32768,"capabilities":["tool_use"]},
                  {"id":"{{coderId}}","object":"model","type":"llm","publisher":"qwen","arch":"qwen3moe","quantization":"Q4_K_M","state":"not-loaded","max_context_length":262144,"capabilities":["tool_use"]},
                  {"id":"google/gemma-3-12b","object":"model","type":"vlm","publisher":"google","arch":"gemma3","quantization":"Q4_K_M","state":"not-loaded","max_context_length":131072},
                  {"id":"text-embedding-nomic-embed-text-v1.5","object":"model","type":"embeddings","publisher":"nomic-ai","state":"not-loaded","max_context_length":2048}
                ]}
                """));

    /// <summary>Ollama with llama3.2 loaded (8K context), qwen3 30B pulled but idle, a cloud model and an embedding model.</summary>
    public static StubHttpMessageHandler Ollama() =>
        StubHttpMessageHandler.Paths(
            ("/v1/models", _ => """{"object":"list","data":[{"id":"llama3.2:latest","object":"model"},{"id":"qwen3:30b-a3b","object":"model"},{"id":"gpt-oss:120b-cloud","object":"model"},{"id":"nomic-embed-text:latest","object":"model"}]}"""),
            ("/api/tags", _ => """
                {"models":[
                  {"name":"llama3.2:latest","model":"llama3.2:latest","details":{"family":"llama","parameter_size":"3.2B","quantization_level":"Q4_K_M"}},
                  {"name":"qwen3:30b-a3b","model":"qwen3:30b-a3b","details":{"family":"qwen3moe","parameter_size":"30.5B","quantization_level":"Q4_K_M"}},
                  {"name":"gpt-oss:120b-cloud","model":"gpt-oss:120b-cloud","remote_model":"gpt-oss:120b","remote_host":"https://ollama.com:443","details":{"family":"gptoss","parameter_size":"116.8B"}},
                  {"name":"nomic-embed-text:latest","model":"nomic-embed-text:latest","details":{"family":"nomic-bert","parameter_size":"137M","quantization_level":"F16"}}
                ]}
                """),
            ("/api/ps", _ => """{"models":[{"name":"llama3.2:latest","model":"llama3.2:latest","size_vram":3000000000,"context_length":8192,"expires_at":"2026-10-08T12:00:00Z"}]}"""),
            ("/api/show", body => body.Contains("llama3.2", StringComparison.Ordinal)
                ? """{"capabilities":["completion","tools"],"model_info":{"general.architecture":"llama","llama.context_length":131072}}"""
                : body.Contains("nomic", StringComparison.Ordinal)
                    ? """{"capabilities":["embedding"],"model_info":{"general.architecture":"nomic-bert","nomic-bert.context_length":2048}}"""
                    : """{"capabilities":["completion","tools","thinking"],"model_info":{"general.architecture":"qwen3moe","qwen3moe.context_length":262144}}"""));
}
