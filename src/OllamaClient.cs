﻿
using OllamaClientLibrary.Constants;
using OllamaClientLibrary.Converters;
using OllamaClientLibrary.Dto.ChatCompletion;
using OllamaClientLibrary.Dto.ChatCompletion.Tools.Request;
using OllamaClientLibrary.Dto.Models;
using OllamaClientLibrary.Dto.PullModel;
using OllamaClientLibrary.HttpClients;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace OllamaClientLibrary
{
    /// <summary>
    /// Represents a client for interacting with the Ollama API.
    /// </summary>
    public class OllamaClient : IDisposable
    {
        private readonly OllamaHttpClient _httpClient;
        private readonly OllamaOptions? _options;

        /// <summary>
        /// Initializes a new instance of the <see cref="OllamaClient"/> class.
        /// </summary>
        /// <param name="options">The options for configuring the client.</param>
        public OllamaClient(OllamaOptions? options = null)
        {
            _options = options ?? new OllamaOptions();

            _httpClient = new OllamaHttpClient(_options);
        }

        /// <summary>
        /// Gets or sets the chat history.
        /// </summary>
        public List<ChatMessage> ChatHistory => _httpClient.ChatHistory;

        /// <summary>
        /// Generates completion text asynchronously.
        /// </summary>
        /// <param name="prompt">The prompt to generate completion for.</param>
        /// <param name="ct">The cancellation token.</param>
        /// <returns>The generated completion text.</returns>
        public async Task<string?> GenerateTextCompletionAsync(string? prompt, CancellationToken ct = default)
        {
            await AutoInstallModelAsync(ct).ConfigureAwait(false);

            return await _httpClient.GenerateTextCompletionAsync(prompt, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Generates completion asynchronously and deserialize the response to the specified type.
        /// </summary>
        /// <typeparam name="T">The type to deserialize the response to.</typeparam>
        /// <param name="prompt">The prompt to generate completion for.</param>
        /// <param name="ct">The cancellation token.</param>
        /// <returns>The generated completion deserialized to the specified type.</returns>
        public async Task<T?> GenerateJsonCompletionAsync<T>(string? prompt, CancellationToken ct = default) where T : class
        {
            await AutoInstallModelAsync(ct).ConfigureAwait(false);

            return await _httpClient.GenerateJsonCompletionAsync<T>(prompt, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Gets chat completion asynchronously.
        /// </summary>
        /// <param name="text">The text to get chat completion for.</param>
        /// <param name="ct">The cancellation token.</param>
        /// <returns>An asynchronous enumerable of chat messages.</returns>
        public async IAsyncEnumerable<ChatMessage?> GetChatCompletionAsync(string text, Tool? tool = null, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await AutoInstallModelAsync(ct).ConfigureAwait(false);

            await foreach (var message in _httpClient.GetChatCompletionAsync(text, tool, ct).ConfigureAwait(false))
            {
                if (tool != null && message?.ToolCalls?.FirstOrDefault()?.Function?.Arguments is { } arguments)
                {
                    var result = Tools.ToolFactory.Invoke(tool, arguments);

                    message.Content = result?.ToString();
                }

                yield return message;
            }
        }

        /// <summary>
        /// Gets chat text completion asynchronously.
        /// </summary>
        /// <param name="text">The text to get chat completion for.</param>
        /// <param name="tool">The tool to use for generating the completion, if any.</param>
        /// <param name="ct">The cancellation token.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the generated chat text completion.</returns>
        public async Task<string?> GetChatTextCompletionAsync(string text, Tool? tool = null, CancellationToken ct = default)
        {
            await AutoInstallModelAsync(ct).ConfigureAwait(false);

            return await _httpClient.GetChatTextCompletionAsync(text, tool, ct).ConfigureAwait(false);
        }


        /// <summary>
        /// Lists models asynchronously.
        /// </summary>
        /// <param name="pattern">The pattern to filter models by name.</param>
        /// <param name="size">The size to filter models by.</param>
        /// <param name="location">The location to filter models by.</param>
        /// <param name="ct">The cancellation token.</param>
        /// <returns>A list of models.</returns>
        public async Task<IEnumerable<Model>> ListModelsAsync(string? pattern = null, ModelSize? size = null, ModelLocation location = ModelLocation.Remote, CancellationToken ct = default)
        {
            IEnumerable<Model> models;

            if (location == ModelLocation.Local)
            {
                models = await _httpClient.ListLocalModelsAsync(ct).ConfigureAwait(false);
            }
            else
            {
                models = await _httpClient.ListRemoteModelsAsync(ct).ConfigureAwait(false);
            }

            if (!string.IsNullOrEmpty(pattern))
            {
                models = models.Where(s => s.Name != null && Regex.IsMatch(s.Name, pattern, RegexOptions.IgnoreCase));
            }

            if (size.HasValue)
            {
                models = size switch
                {
                    ModelSize.Tiny => models.Where(model => model.Size.HasValue && SizeConverter.BytesToGigabytes(model.Size.Value) <= 0.5),
                    ModelSize.Small => models.Where(model => model.Size.HasValue && SizeConverter.BytesToGigabytes(model.Size.Value) > 0.5 && SizeConverter.BytesToGigabytes(model.Size.Value) <= 2),
                    ModelSize.Medium => models.Where(model => model.Size.HasValue && SizeConverter.BytesToGigabytes(model.Size.Value) > 2 && SizeConverter.BytesToGigabytes(model.Size.Value) <= 5),
                    ModelSize.Large => models.Where(model => model.Size.HasValue && SizeConverter.BytesToGigabytes(model.Size.Value) > 5),
                    _ => models
                };
            }

            return models.OrderBy(s => s.Size ?? 0).ToList();
        }

        /// <summary>
        /// Gets embeddings for the specified input asynchronously.
        /// </summary>
        /// <param name="input">The input text to generate embeddings for.</param>
        /// <param name="ct">The cancellation token.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains a jagged array of doubles representing the embeddings.</returns>
        public async Task<double[][]> GetEmbeddingAsync(string[] input, CancellationToken ct = default)
        {
            await AutoInstallModelAsync(ct).ConfigureAwait(false);

            return await _httpClient.GetEmbeddingAsync(input, ct).ConfigureAwait(false);
        }

        public async Task PullModelAsync(string modelName, IProgress<PullModelProgress>? progress = null, CancellationToken ct = default)
        {
            var models = await _httpClient.ListLocalModelsAsync(ct).ConfigureAwait(false);

            if (models == null || !models.Any(model => string.Equals(model.Name, modelName, StringComparison.OrdinalIgnoreCase)))
            {
                await _httpClient.PullModelAsync(modelName, progress, ct).ConfigureAwait(false);
            }
            else
            {
                progress?.Report(new PullModelProgress()
                {
                    Status = $"The model {modelName} is already installed",
                    Percentage = 100
                });
            }

        }

        /// <summary>
        /// Disposes the resources used by the <see cref="OllamaClient"/> class.
        /// </summary>
        public void Dispose()
        {
            _httpClient.Dispose();

            GC.SuppressFinalize(this);
        }

        private async Task AutoInstallModelAsync(CancellationToken ct = default)
        {
            if (_options != null && _options.AutoInstallModel)
            {
                var model = _options?.Model ?? "qwen2.5:1.5b";

                await PullModelAsync(model, null, ct: ct).ConfigureAwait(false);
            }
        }
    }
}