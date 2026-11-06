using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Provance.Core.Data;
using Provance.Core.Exceptions;
using Provance.Core.Services.Interfaces;
using Provance.Core.Verification;

namespace Provance.AspNetCore.Middleware.Tests
{
    public class AuditMiddlewareTests
    {
        private static DefaultHttpContext Request(string path = "/api/orders", string query = "?token=SECRET", string method = "POST")
        {
            var context = new DefaultHttpContext();
            context.Request.Method = method;
            context.Request.Path = path;
            context.Request.QueryString = new QueryString(query);
            context.Request.Headers.UserAgent = "unit-test-agent";
            context.Connection.RemoteIpAddress = IPAddress.Parse("10.1.2.3");
            return context;
        }

        private static ProvanceAuditMiddleware Middleware(RequestDelegate next, Action<HttpAuditOptions>? configure = null)
        {
            var options = new HttpAuditOptions();
            configure?.Invoke(options);
            return new ProvanceAuditMiddleware(next, Options.Create(options), NullLogger<ProvanceAuditMiddleware>.Instance);
        }

        [Fact]
        public async Task Completed_request_is_recorded_without_personal_data_by_default()
        {
            var ledger = new RecordingLedger();
            var middleware = Middleware(ctx => { ctx.Response.StatusCode = 201; return Task.CompletedTask; });

            await middleware.InvokeAsync(Request(), ledger, new FakeLifetime());

            var (eventType, payload) = Assert.Single(ledger.Entries);
            Assert.Equal(HttpRequestAuditPayload.CompletedEvent, eventType);
            Assert.Equal(201, payload.StatusCode);
            Assert.Equal("POST", payload.Method);
            Assert.Equal("/api/orders", payload.Path);
            Assert.Equal("completed", payload.Outcome);
            Assert.Null(payload.QueryString);
            Assert.Null(payload.ClientIp);
            Assert.Null(payload.UserAgent);
            Assert.Null(payload.ActorId);
        }

        [Fact]
        public async Task Optional_fields_are_recorded_when_enabled()
        {
            var ledger = new RecordingLedger();
            var middleware = Middleware(_ => Task.CompletedTask, o =>
            {
                o.IncludeQueryString = true;
                o.IncludeClientIp = true;
                o.IncludeUserAgent = true;
            });

            await middleware.InvokeAsync(Request(), ledger, new FakeLifetime());

            var payload = Assert.Single(ledger.Entries).Payload;
            Assert.Equal("?token=SECRET", payload.QueryString);
            Assert.Equal("10.1.2.3", payload.ClientIp);
            Assert.Equal("unit-test-agent", payload.UserAgent);
        }

        [Fact]
        public async Task Unhandled_exception_is_recorded_as_500_and_rethrown()
        {
            var ledger = new RecordingLedger();
            var middleware = Middleware(_ => throw new InvalidOperationException("contains personal data"));

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(Request(), ledger, new FakeLifetime()));

            Assert.Equal("contains personal data", ex.Message);
            var (eventType, payload) = Assert.Single(ledger.Entries);
            Assert.Equal(HttpRequestAuditPayload.FailedEvent, eventType);
            Assert.Equal(500, payload.StatusCode);
            Assert.Equal(typeof(InvalidOperationException).FullName, payload.ExceptionType);
        }

        [Fact]
        public async Task Ledger_outage_does_not_fail_the_request_by_default()
        {
            var middleware = Middleware(_ => Task.CompletedTask);
            await middleware.InvokeAsync(Request(), new FailingLedger(), new FakeLifetime());
        }

        [Fact]
        public async Task Ledger_outage_fails_the_request_in_throw_mode()
        {
            var middleware = Middleware(_ => Task.CompletedTask, o => o.FailureMode = HttpAuditFailureMode.Throw);
            await Assert.ThrowsAsync<LedgerUnavailableException>(() => middleware.InvokeAsync(Request(), new FailingLedger(), new FakeLifetime()));
        }

        [Fact]
        public async Task Ledger_outage_never_hides_the_original_exception()
        {
            var middleware = Middleware(_ => throw new InvalidOperationException("original"), o => o.FailureMode = HttpAuditFailureMode.Throw);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(Request(), new FailingLedger(), new FakeLifetime()));
            Assert.Equal("original", ex.Message);
        }

        [Fact]
        public async Task Slow_ledger_is_bounded_by_the_audit_timeout()
        {
            var middleware = Middleware(_ => Task.CompletedTask, o => o.AuditTimeout = TimeSpan.FromMilliseconds(100));
            var watch = System.Diagnostics.Stopwatch.StartNew();

            await middleware.InvokeAsync(Request(), new HangingLedger(), new FakeLifetime());

            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
        }

        [Theory]
        [InlineData("/health")]
        [InlineData("/health/ready")]
        [InlineData("/metrics")]
        public async Task Excluded_paths_are_not_recorded(string path)
        {
            var ledger = new RecordingLedger();
            await Middleware(_ => Task.CompletedTask).InvokeAsync(Request(path), ledger, new FakeLifetime());
            Assert.Empty(ledger.Entries);
        }

        [Fact]
        public async Task Custom_predicate_filters_requests()
        {
            var ledger = new RecordingLedger();
            var middleware = Middleware(_ => Task.CompletedTask, o => o.ShouldAudit = ctx => !HttpMethods.IsGet(ctx.Request.Method));

            await middleware.InvokeAsync(Request(method: "GET"), ledger, new FakeLifetime());
            await middleware.InvokeAsync(Request(method: "DELETE"), ledger, new FakeLifetime());

            Assert.Equal("DELETE", Assert.Single(ledger.Entries).Payload.Method);
        }

        [Theory]
        [InlineData(ClaimTypes.NameIdentifier, "user-1")]
        [InlineData("sub", "user-2")]
        public async Task Actor_comes_from_the_identity_claims(string claimType, string value)
        {
            var ledger = new RecordingLedger();
            var context = Request();
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("email", "someone@example.org"), new Claim(claimType, value)], "test"));

            await Middleware(_ => Task.CompletedTask).InvokeAsync(context, ledger, new FakeLifetime());

            Assert.Equal(value, Assert.Single(ledger.Entries).Payload.ActorId);
        }

        [Fact]
        public async Task Unrelated_claims_are_never_used_as_actor()
        {
            var ledger = new RecordingLedger();
            var context = Request();
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("email", "someone@example.org")], "test"));

            await Middleware(_ => Task.CompletedTask).InvokeAsync(context, ledger, new FakeLifetime());

            Assert.Null(Assert.Single(ledger.Entries).Payload.ActorId);
        }

        [Fact]
        public async Task Client_abort_is_recorded_as_aborted()
        {
            var ledger = new RecordingLedger();
            var context = Request();
            using var aborted = new CancellationTokenSource();
            context.RequestAborted = aborted.Token;
            var middleware = Middleware(ctx =>
            {
                aborted.Cancel();
                ctx.RequestAborted.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            });

            await Assert.ThrowsAsync<OperationCanceledException>(() => middleware.InvokeAsync(context, ledger, new FakeLifetime()));

            var (eventType, payload) = Assert.Single(ledger.Entries);
            Assert.Equal(HttpRequestAuditPayload.AbortedEvent, eventType);
            Assert.Equal("aborted", payload.Outcome);
        }

        [Fact]
        public async Task End_to_end_with_the_real_ledger()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddProvance(o =>
            {
                o.LedgerId = "middleware-test";
                o.SecretKey = "middleware-test-secret-key-0123456789abcdef";
            }).UseInMemoryStore().AddHttpAuditing(o => o.IncludeClientIp = true);
            await using var provider = services.BuildServiceProvider();
            var hosted = provider.GetServices<IHostedService>().ToList();
            foreach (var service in hosted) await service.StartAsync(CancellationToken.None);

            var ledger = provider.GetRequiredService<ILedgerService>();
            var middleware = new ProvanceAuditMiddleware(_ => Task.CompletedTask,
                provider.GetRequiredService<IOptions<HttpAuditOptions>>(), NullLogger<ProvanceAuditMiddleware>.Instance);

            await middleware.InvokeAsync(Request("/api/a"), ledger, new FakeLifetime());
            await middleware.InvokeAsync(Request("/api/b"), ledger, new FakeLifetime());

            var head = await ledger.GetHeadAsync();
            var payload = head!.GetPayload<HttpRequestAuditPayload>();
            Assert.Equal("/api/b", payload!.Path);
            Assert.Equal("10.1.2.3", payload.ClientIp);

            LedgerVerificationResult result = await ledger.VerifyAsync();
            Assert.True(result.IsValid, result.Message);
            Assert.Equal(2, result.EntriesVerified);

            foreach (var service in hosted) await service.StopAsync(CancellationToken.None);
        }

        private sealed class RecordingLedger : ILedgerService
        {
            public List<(string EventType, HttpRequestAuditPayload Payload)> Entries { get; } = [];

            public Task<LedgerEntry> AddEntryAsync<TPayload>(string eventType, TPayload payload, CancellationToken cancellationToken = default)
                where TPayload : notnull
            {
                Entries.Add((eventType, (HttpRequestAuditPayload)(object)payload));
                return Task.FromResult<LedgerEntry>(null!);
            }

            public Task<LedgerEntry?> GetHeadAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task<LedgerEntry?> GetEntryAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task<LedgerVerificationResult> VerifyAsync(LedgerVerificationOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task<LedgerCheckpoint?> CreateCheckpointAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task<bool> RedactPayloadAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        }

        private class FailingLedger : ILedgerService
        {
            public virtual Task<LedgerEntry> AddEntryAsync<TPayload>(string eventType, TPayload payload, CancellationToken cancellationToken = default)
                where TPayload : notnull
                => throw new LedgerUnavailableException("store down");

            public Task<LedgerEntry?> GetHeadAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task<LedgerEntry?> GetEntryAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task<LedgerVerificationResult> VerifyAsync(LedgerVerificationOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task<LedgerCheckpoint?> CreateCheckpointAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task<bool> RedactPayloadAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        }

        private sealed class HangingLedger : FailingLedger
        {
            public override async Task<LedgerEntry> AddEntryAsync<TPayload>(string eventType, TPayload payload, CancellationToken cancellationToken = default)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                throw new InvalidOperationException("unreachable");
            }
        }

        private sealed class FakeLifetime : IHostApplicationLifetime
        {
            public CancellationToken ApplicationStarted => CancellationToken.None;
            public CancellationToken ApplicationStopping => CancellationToken.None;
            public CancellationToken ApplicationStopped => CancellationToken.None;
            public void StopApplication() { }
        }
    }
}
