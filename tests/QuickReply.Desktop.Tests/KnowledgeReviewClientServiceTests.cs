using System.Net;
using System.Net.Http.Headers;
using QuickReply.Desktop.Models;
using QuickReply.Desktop.Services;
using QuickReply.Desktop.Tests.TestSupport;

namespace QuickReply.Desktop.Tests;

public sealed class KnowledgeReviewClientServiceTests
{
    [Fact]
    public async Task GetReviewQueueAsync_UsesBuiltInServer_WhenServerBaseUrlIsEmpty()
    {
        var handler = new RecordingHttpMessageHandler((request, _) =>
        {
            Assert.Equal($"{QuickReplyServiceEndpointDefaults.DefaultRemoteServiceBaseUrl}/api/quickreply/knowledge/review/queue?limit=200&cursor=", request.RequestUri?.ToString());
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("token-1", request.Headers.Authorization?.Parameter);
            return Task.FromResult(HttpResponseFactory.Json(
                """
                {
                  "items": [
                    {
                    "id": "11111111-1111-1111-1111-111111111111",
                    "status": "reviewed",
                    "platform": "wecom",
                    "productKey": "SKU-1",
                    "questionNormalized": "可以开发票吗",
                    "answerDraft": "支持开发票",
                    "tags": ["发票"],
                    "confidence": 0.9,
                    "riskLevel": "medium",
                    "sourceCount": 2,
                    "clusterKey": "wecom|sku-1|invoice",
                    "evidenceSummary": "2 条样本",
                    "updatedAt": "2026-04-16T12:00:00+08:00"
                    }
                  ],
                  "nextCursor": "",
                  "hasMore": false
                }
                """));
        });
        var service = new KnowledgeReviewClientService((baseUri, token) =>
        {
            var client = new HttpClient(handler)
            {
                BaseAddress = baseUri
            };
            if (!string.IsNullOrWhiteSpace(token))
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            return client;
        });

        var queue = await service.GetReviewQueueAsync(string.Empty, "token-1");

        var item = Assert.Single(queue);
        Assert.Equal("可以开发票吗", item.QuestionNormalized);
    }

    [Fact]
    public async Task GetReviewQueueAsync_FallsBackToLocal_WhenConfiguredServerReturnsNotFound()
    {
        var requestCount = 0;
        var handler = new RecordingHttpMessageHandler((request, _) =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                Assert.Equal("https://server.example.com/api/quickreply/knowledge/review/queue?limit=200&cursor=", request.RequestUri?.ToString());
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            Assert.Equal("http://127.0.0.1:5128/api/quickreply/knowledge/review/queue?limit=200&cursor=", request.RequestUri?.ToString());
            return Task.FromResult(HttpResponseFactory.Json(
                """
                {
                  "items": [
                    {
                    "id": "33333333-3333-3333-3333-333333333333",
                    "status": "draft",
                    "platform": "qianniu",
                    "productKey": "",
                    "questionNormalized": "今天能发吗",
                    "answerDraft": "今天可以安排发出。",
                    "tags": ["发货"],
                    "confidence": 0.8,
                    "riskLevel": "low",
                    "sourceCount": 1,
                    "clusterKey": "qianniu||shipping",
                    "evidenceSummary": "1 条样本",
                    "updatedAt": "2026-04-16T12:00:00+08:00"
                    }
                  ],
                  "nextCursor": "",
                  "hasMore": false
                }
                """));
        });
        var service = new KnowledgeReviewClientService((baseUri, _) => new HttpClient(handler)
        {
            BaseAddress = baseUri
        });

        var queue = await service.GetReviewQueueAsync("https://server.example.com", null);

        var item = Assert.Single(queue);
        Assert.Equal("今天能发吗", item.QuestionNormalized);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ApproveAndRejectAsync_PostToExpectedEndpoints()
    {
        var handler = new RecordingHttpMessageHandler((request, _) =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        var service = new KnowledgeReviewClientService((baseUri, _) => new HttpClient(handler)
        {
            BaseAddress = baseUri
        });
        var candidateId = Guid.Parse("22222222-2222-2222-2222-222222222222");

        await service.ApproveAsync(
            "https://server.example.com",
            null,
            candidateId,
            new ApproveKnowledgeCandidateRequest
            {
                AnswerFinal = "您好，支持开票。",
                Synonyms = ["发票"],
                Tags = ["发票"],
                UsageNotes = "仅限企业客户",
                ReviewComment = "ok",
                Version = "v1"
            });
        await service.RejectAsync(
            "https://server.example.com",
            null,
            candidateId,
            new RejectKnowledgeCandidateRequest
            {
                Reason = "证据不足",
                ReviewComment = "need more"
            });

        Assert.Collection(
            handler.Requests,
            request => Assert.Equal($"https://server.example.com/api/quickreply/knowledge/review/{candidateId}/approve", request.RequestUri?.ToString()),
            request => Assert.Equal($"https://server.example.com/api/quickreply/knowledge/review/{candidateId}/reject", request.RequestUri?.ToString()));
    }

    [Fact]
    public async Task GetReviewHistoryAsync_AccumulatesPagedHistory()
    {
        var candidateId = Guid.Parse("66666666-7777-8888-9999-000000000000");
        var requestCount = 0;
        var handler = new RecordingHttpMessageHandler((request, _) =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                Assert.Equal(
                    $"https://server.example.com/api/quickreply/knowledge/review/{candidateId}/history?limit=100&cursor=",
                    request.RequestUri?.ToString());
                return Task.FromResult(HttpResponseFactory.Json(
                    """
                    {
                      "candidateId": "66666666-7777-8888-9999-000000000000",
                      "items": [
                        {
                          "id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                          "candidateId": "66666666-7777-8888-9999-000000000000",
                          "actionType": "approved",
                          "reviewer": "tester@machine",
                          "reviewSessionId": "session-1",
                          "reviewComment": "ok",
                          "reviewReason": "",
                          "beforeSnapshotJson": "{}",
                          "afterSnapshotJson": "{}",
                          "createdAt": "2026-04-17T10:00:00+08:00"
                        }
                      ],
                      "nextCursor": "cursor-2",
                      "hasMore": true
                    }
                    """));
            }

            Assert.Equal(
                $"https://server.example.com/api/quickreply/knowledge/review/{candidateId}/history?limit=100&cursor=cursor-2",
                request.RequestUri?.ToString());
            return Task.FromResult(HttpResponseFactory.Json(
                """
                {
                  "candidateId": "66666666-7777-8888-9999-000000000000",
                  "items": [
                    {
                      "id": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                      "candidateId": "66666666-7777-8888-9999-000000000000",
                      "actionType": "unpublished",
                      "reviewer": "tester@machine",
                      "reviewSessionId": "session-2",
                      "reviewComment": "下线",
                      "reviewReason": "过期",
                      "beforeSnapshotJson": "{}",
                      "afterSnapshotJson": "{}",
                      "createdAt": "2026-04-17T11:00:00+08:00"
                    }
                  ],
                  "nextCursor": "",
                  "hasMore": false
                }
                """));
        });
        var service = new KnowledgeReviewClientService((baseUri, _) => new HttpClient(handler)
        {
            BaseAddress = baseUri
        });

        var history = await service.GetReviewHistoryAsync("https://server.example.com", null, candidateId);

        Assert.Equal(2, history.Count);
        Assert.Equal("approved", history[0].ActionType);
        Assert.Equal("unpublished", history[1].ActionType);
    }

    [Fact]
    public async Task RemovePublishedAsync_PostsToExpectedEndpoint()
    {
        var handler = new RecordingHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var service = new KnowledgeReviewClientService((baseUri, _) => new HttpClient(handler)
        {
            BaseAddress = baseUri
        });
        var candidateId = Guid.Parse("77777777-7777-7777-7777-777777777777");

        await service.RemovePublishedAsync(
            "https://server.example.com",
            null,
            candidateId,
            new RemovePublishedKnowledgeCardRequest
            {
                Reason = "过期",
                ReviewComment = "停售下线"
            });

        var request = Assert.Single(handler.Requests);
        Assert.Equal(
            $"https://server.example.com/api/quickreply/knowledge/published/{candidateId}/remove",
            request.RequestUri?.ToString());
    }

    [Fact]
    public async Task GetReviewQueueAsync_AttemptsToStartLocalService_WhenLocalEndpointIsDown()
    {
        var localAttempts = 0;
        var bootstrapCalls = 0;
        var handler = new RecordingHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri?.ToString() == "http://127.0.0.1:5128/api/quickreply/knowledge/review/queue?limit=200&cursor=")
            {
                localAttempts++;
                if (localAttempts == 1)
                {
                    throw new HttpRequestException("actively refused");
                }

                return Task.FromResult(HttpResponseFactory.Json(
                    """
                    {
                      "items": [
                        {
                        "id": "44444444-4444-4444-4444-444444444444",
                        "status": "reviewed",
                        "candidateKind": "resource",
                        "platform": "qianniu",
                        "productKey": "",
                        "questionNormalized": "智骑软件",
                        "answerDraft": "智骑软件下载地址：https://example.com/app",
                        "tags": ["资料"],
                        "confidence": 0.6,
                        "riskLevel": "low",
                        "sourceCount": 2,
                        "clusterKey": "qianniu||resource|智骑软件",
                        "evidenceSummary": "2 条样本",
                        "updatedAt": "2026-04-16T12:00:00+08:00"
                        }
                      ],
                      "nextCursor": "",
                      "hasMore": false
                    }
                    """));
            }

            throw new InvalidOperationException("unexpected request");
        });
        var service = new KnowledgeReviewClientService(
            (baseUri, _) => new HttpClient(handler)
            {
                BaseAddress = baseUri
            },
            _ =>
            {
                bootstrapCalls++;
                return Task.FromResult(true);
            });

        var queue = await service.GetReviewQueueAsync(KnowledgeReviewClientService.DefaultLocalReviewBaseUrl, null);

        Assert.Single(queue);
        Assert.Equal(1, bootstrapCalls);
        Assert.Equal(2, localAttempts);
    }

    [Fact]
    public async Task RejectAsync_AttemptsToStartLocalService_WhenLocalEndpointIsDown()
    {
        var localAttempts = 0;
        var bootstrapCalls = 0;
        var candidateId = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var handler = new RecordingHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri?.ToString() == $"http://127.0.0.1:5128/api/quickreply/knowledge/review/{candidateId}/reject")
            {
                localAttempts++;
                if (localAttempts == 1)
                {
                    throw new HttpRequestException("actively refused");
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }

            throw new InvalidOperationException("unexpected request");
        });
        var service = new KnowledgeReviewClientService(
            (baseUri, _) => new HttpClient(handler)
            {
                BaseAddress = baseUri
            },
            _ =>
            {
                bootstrapCalls++;
                return Task.FromResult(true);
            });

        await service.RejectAsync(
            KnowledgeReviewClientService.DefaultLocalReviewBaseUrl,
            null,
            candidateId,
            new RejectKnowledgeCandidateRequest
            {
                Reason = "证据不足"
            });

        Assert.Equal(1, bootstrapCalls);
        Assert.Equal(2, localAttempts);
    }
}
