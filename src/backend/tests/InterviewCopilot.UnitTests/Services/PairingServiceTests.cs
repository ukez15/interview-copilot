using FluentAssertions;
using InterviewCopilot.Application.Common.Exceptions;
using InterviewCopilot.Application.Common.Interfaces;
using InterviewCopilot.Application.Pairing;
using InterviewCopilot.Domain.Entities;
using InterviewCopilot.Domain.Exceptions;

namespace InterviewCopilot.UnitTests.Services;

public sealed class PairingServiceTests
{
    private static readonly Guid Owner = Guid.NewGuid();
    private readonly MutableClock _clock = new(FixedClock.Default);
    private readonly FakeBrowsers _browsers = new();
    private readonly PairingCodeStore _codes;
    private readonly PairingService _service;

    public PairingServiceTests()
    {
        _codes = new PairingCodeStore(_clock);
        _service = new PairingService(_browsers, _browsers, _codes, _clock);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Should_IssueSixDigitCode_ExpiringInFiveMinutes()
    {
        var issued = _service.IssueCode(Owner);

        issued.Code.Should().MatchRegex("^[0-9]{6}$");
        issued.ExpiresAt.Should().Be(_clock.GetUtcNow().AddMinutes(5));
    }

    [Fact]
    public async Task Should_ReplaceOldCode_When_NewOneIssued()
    {
        var first = _service.IssueCode(Owner);
        var second = _service.IssueCode(Owner);

        if (first.Code != second.Code)
        {
            var act = () => _service.ClaimAsync(first.Code, "Chrome", Ct);
            await act.Should().ThrowAsync<DomainException>();
        }

        var token = await _service.ClaimAsync(second.Code, "Chrome", Ct);
        token.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Should_CreateBrowser_StoreOnlyHash_And_ReturnTokenOnce()
    {
        var issued = _service.IssueCode(Owner);

        var token = await _service.ClaimAsync(issued.Code, " Edge ", Ct);

        token.Should().MatchRegex("^[A-Za-z0-9_-]{43}$"); // 32 bytes, base64url without padding
        var browser = _browsers.Items.Single();
        browser.UserId.Should().Be(Owner);
        browser.Name.Should().Be("Edge");
        browser.TokenHash.Should().Be(PairingService.HashToken(token)).And.MatchRegex("^[0-9a-f]{64}$");
        browser.TokenHash.Should().NotContain(token);
        _browsers.SaveCount.Should().Be(1);
    }

    [Fact]
    public async Task Should_RejectReusedCode()
    {
        var issued = _service.IssueCode(Owner);
        await _service.ClaimAsync(issued.Code, "Chrome", Ct);

        var act = () => _service.ClaimAsync(issued.Code, "Chrome", Ct);

        (await act.Should().ThrowAsync<DomainException>()).WithMessage(PairingService.InvalidCodeMessage);
        _browsers.Items.Should().HaveCount(1);
    }

    [Fact]
    public async Task Should_RejectExpiredCode()
    {
        var issued = _service.IssueCode(Owner);
        _clock.Advance(TimeSpan.FromMinutes(5));

        var act = () => _service.ClaimAsync(issued.Code, "Chrome", Ct);

        (await act.Should().ThrowAsync<DomainException>()).WithMessage(PairingService.InvalidCodeMessage);
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("abcdef")]
    public async Task Should_RejectUnknownOrMalformedCode(string code)
    {
        var act = () => _service.ClaimAsync(code, "Chrome", Ct);

        (await act.Should().ThrowAsync<DomainException>()).WithMessage(PairingService.InvalidCodeMessage);
    }

    [Fact]
    public async Task Should_KeepCode_When_BrowserNameIsInvalid()
    {
        var issued = _service.IssueCode(Owner);

        var bad = () => _service.ClaimAsync(issued.Code, "  ", Ct);
        await bad.Should().ThrowAsync<DomainException>();
        var token = await _service.ClaimAsync(issued.Code, "Firefox", Ct);

        token.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Should_ListOnlyOwnNonRevokedBrowsers()
    {
        var mine = await PairAsync(Owner, "Chrome");
        var revoked = await PairAsync(Owner, "Edge");
        await PairAsync(Guid.NewGuid(), "Someone else");
        await _service.RevokeAsync(Owner, _browsers.Items.Single(b => b.Name == "Edge").Id, Ct);

        var list = await _service.ListAsync(Owner, Ct);

        list.Should().ContainSingle().Which.Name.Should().Be("Chrome");
        mine.Should().NotBeEmpty();
        revoked.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Should_Revoke_And_ThenRejectTheToken()
    {
        var token = await PairAsync(Owner, "Chrome");
        var id = _browsers.Items.Single().Id;

        await _service.RevokeAsync(Owner, id, Ct);

        _browsers.Items.Single().IsRevoked.Should().BeTrue();
        (await _service.AuthenticateAsync(token, Ct)).Should().BeNull();
    }

    [Fact]
    public async Task Should_NotRevokeSomeoneElsesBrowser()
    {
        await PairAsync(Owner, "Chrome");
        var id = _browsers.Items.Single().Id;

        var act = () => _service.RevokeAsync(Guid.NewGuid(), id, Ct);

        await act.Should().ThrowAsync<NotFoundException>();
        _browsers.Items.Single().IsRevoked.Should().BeFalse();
    }

    [Fact]
    public async Task Should_ThrowNotFound_When_RevokingUnknownBrowser()
    {
        var act = () => _service.RevokeAsync(Owner, Guid.NewGuid(), Ct);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Should_AuthenticateKnownToken_And_RejectUnknownOnes()
    {
        var token = await PairAsync(Owner, "Chrome");

        var found = await _service.AuthenticateAsync(token, Ct);
        var unknown = await _service.AuthenticateAsync("not-a-real-token", Ct);
        var blank = await _service.AuthenticateAsync(" ", Ct);

        found.Should().NotBeNull();
        found.UserId.Should().Be(Owner);
        unknown.Should().BeNull();
        blank.Should().BeNull();
    }

    [Fact]
    public async Task Should_UpdateLastUsed_AtMostOncePerFiveMinutes()
    {
        var token = await PairAsync(Owner, "Chrome");
        var browser = _browsers.Items.Single();
        var savesAfterPairing = _browsers.SaveCount;

        await _service.AuthenticateAsync(token, Ct);
        var firstUse = _clock.GetUtcNow();
        browser.LastUsedAt.Should().Be(firstUse);

        _clock.Advance(TimeSpan.FromMinutes(4));
        await _service.AuthenticateAsync(token, Ct);
        browser.LastUsedAt.Should().Be(firstUse);
        _browsers.SaveCount.Should().Be(savesAfterPairing + 1);

        _clock.Advance(TimeSpan.FromMinutes(1));
        await _service.AuthenticateAsync(token, Ct);
        browser.LastUsedAt.Should().Be(_clock.GetUtcNow());
        _browsers.SaveCount.Should().Be(savesAfterPairing + 2);
    }

    [Fact]
    public void Should_NotIssueTheSameCodeToTwoUsers_WhileBothAreActive()
    {
        var codes = Enumerable.Range(0, 200).Select(_ => _codes.Issue(Guid.NewGuid()).Code).ToList();

        codes.Should().OnlyHaveUniqueItems();
    }

    private async Task<string> PairAsync(Guid userId, string name)
    {
        var issued = _service.IssueCode(userId);
        return await _service.ClaimAsync(issued.Code, name, Ct);
    }

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class FakeBrowsers : IPairedBrowserRepository, IUnitOfWork
    {
        public List<PairedBrowser> Items { get; } = [];

        public int SaveCount { get; private set; }

        public Task<PairedBrowser?> GetAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(Items.Find(b => b.Id == id));

        public Task<PairedBrowser?> FindByTokenHashAsync(string tokenHash, CancellationToken cancellationToken) =>
            Task.FromResult(Items.Find(b => b.TokenHash == tokenHash));

        public Task<IReadOnlyList<PairedBrowser>> ListAsync(Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PairedBrowser>>([.. Items.Where(b => b.UserId == userId)]);

        public void Add(PairedBrowser browser) => Items.Add(browser);

        public Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            SaveCount++;
            return Task.CompletedTask;
        }
    }
}
