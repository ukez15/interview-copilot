using System.Net;
using FluentAssertions;
using InterviewCopilot.Infrastructure.Web;

namespace InterviewCopilot.UnitTests.Infrastructure;

public class BlockedAddressesTests
{
    [Theory]
    // 0.0.0.0/8
    [InlineData("0.0.0.0")]
    [InlineData("0.255.255.255")]
    // 10.0.0.0/8
    [InlineData("10.0.0.0")]
    [InlineData("10.255.255.255")]
    // 100.64.0.0/10
    [InlineData("100.64.0.0")]
    [InlineData("100.127.255.255")]
    // 127.0.0.0/8
    [InlineData("127.0.0.1")]
    [InlineData("127.255.255.255")]
    // 169.254.0.0/16
    [InlineData("169.254.0.0")]
    [InlineData("169.254.169.254")]
    [InlineData("169.254.255.255")]
    // 172.16.0.0/12
    [InlineData("172.16.0.0")]
    [InlineData("172.31.255.255")]
    // 192.0.0.0/24
    [InlineData("192.0.0.0")]
    [InlineData("192.0.0.255")]
    // 192.168.0.0/16
    [InlineData("192.168.0.0")]
    [InlineData("192.168.255.255")]
    // 198.18.0.0/15
    [InlineData("198.18.0.0")]
    [InlineData("198.19.255.255")]
    // 224.0.0.0/4 and 240.0.0.0/4 (including broadcast)
    [InlineData("224.0.0.0")]
    [InlineData("239.255.255.255")]
    [InlineData("240.0.0.0")]
    [InlineData("255.255.255.255")]
    // IPv6
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("fc00::")]
    [InlineData("fdff:ffff:ffff:ffff:ffff:ffff:ffff:ffff")]
    [InlineData("fe80::1")]
    [InlineData("febf:ffff:ffff:ffff:ffff:ffff:ffff:ffff")]
    [InlineData("ff00::")]
    [InlineData("ff02::1")]
    // IPv4-mapped IPv6 follows the IPv4 rules
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:10.1.2.3")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("::ffff:192.168.1.1")]
    [InlineData("::ffff:100.64.0.1")]
    [InlineData("::ffff:0.0.0.0")]
    // IPv4-compatible, NAT64 and 6to4 forms of blocked addresses
    [InlineData("::127.0.0.1")]
    [InlineData("::8.8.8.8")]
    [InlineData("64:ff9b::7f00:1")]
    [InlineData("64:ff9b::a9fe:a9fe")]
    [InlineData("2002:7f00:1::")]
    [InlineData("2002:c0a8:101::1")]
    public void Should_Block(string address)
    {
        BlockedAddresses.IsBlocked(IPAddress.Parse(address)).Should().BeTrue();
    }

    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("8.8.8.8")]
    [InlineData("93.184.216.34")]
    // just outside 0/8, 10/8
    [InlineData("1.0.0.0")]
    [InlineData("9.255.255.255")]
    [InlineData("11.0.0.0")]
    // just outside 100.64/10
    [InlineData("100.63.255.255")]
    [InlineData("100.128.0.0")]
    // just outside 127/8
    [InlineData("126.255.255.255")]
    [InlineData("128.0.0.0")]
    // just outside 169.254/16
    [InlineData("169.253.255.255")]
    [InlineData("169.255.0.0")]
    // just outside 172.16/12
    [InlineData("172.15.255.255")]
    [InlineData("172.32.0.0")]
    // just outside 192.0.0/24
    [InlineData("191.255.255.255")]
    [InlineData("192.0.1.0")]
    // just outside 192.168/16
    [InlineData("192.167.255.255")]
    [InlineData("192.169.0.0")]
    // just outside 198.18/15
    [InlineData("198.17.255.255")]
    [InlineData("198.20.0.0")]
    // just outside 224/4
    [InlineData("223.255.255.255")]
    // public IPv6
    [InlineData("2606:4700:4700::1111")]
    [InlineData("2001:4860:4860::8888")]
    // just outside fc00::/7, fe80::/10, ff00::/8
    [InlineData("fbff:ffff:ffff:ffff:ffff:ffff:ffff:ffff")]
    [InlineData("fe00::1")]
    [InlineData("feff::1")]
    // mapped / embedded public IPv4
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("::ffff:93.184.216.34")]
    [InlineData("64:ff9b::808:808")]
    [InlineData("2002:808:808::1")]
    public void Should_Allow(string address)
    {
        BlockedAddresses.IsBlocked(IPAddress.Parse(address)).Should().BeFalse();
    }

    [Fact]
    public void Should_ThrowOnNull()
    {
        var act = () => BlockedAddresses.IsBlocked(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
