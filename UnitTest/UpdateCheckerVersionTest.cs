using Microsoft.VisualStudio.TestTools.UnitTesting;
using AppUpdateChecker = Shadowsocks.Controller.HttpRequest.UpdateChecker;

namespace UnitTest;

[TestClass]
public class UpdateCheckerVersionTest
{
    [TestMethod]
    public void SameReleaseIgnoresLeadingV()
    {
        Assert.IsTrue(AppUpdateChecker.IsSameRelease("v6.1.0-net10", "6.1.0-net10"));
        Assert.IsFalse(AppUpdateChecker.IsReleaseNewer("6.1.0-net10", "v6.1.0-net10"));
    }

    [TestMethod]
    public void BaseUpstreamTagWithSameNumericVersionIsNotNewer()
    {
        Assert.IsFalse(AppUpdateChecker.IsReleaseNewer("6.1.0-net10", "6.1.0"));
        Assert.IsFalse(AppUpdateChecker.IsReleaseNewer("6.1.0-net10", "v6.1.0"));
    }

    [TestMethod]
    public void PatchVersionIncreaseIsNewer()
    {
        Assert.IsTrue(AppUpdateChecker.IsReleaseNewer("6.1.0-net10", "6.1.1-net10"));
    }

    [TestMethod]
    public void MinorVersionIncreaseIsNewer()
    {
        Assert.IsTrue(AppUpdateChecker.IsReleaseNewer("6.1.0-net10", "6.2.0"));
    }
}
