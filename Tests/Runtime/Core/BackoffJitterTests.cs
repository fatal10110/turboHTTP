using System.Collections.Generic;
using NUnit.Framework;
using TurboHTTP.Core.Internal;

namespace TurboHTTP.Tests.Core
{
    public class BackoffJitterTests
    {
        [Test]
        public void NextDouble_StaysInUnitInterval_AndVaries()
        {
            var seen = new HashSet<double>();
            for (int i = 0; i < 256; i++)
            {
                double value = BackoffJitter.NextDouble();
                Assert.That(value, Is.GreaterThanOrEqualTo(0d).And.LessThan(1d));
                seen.Add(value);
            }

            Assert.That(seen.Count, Is.GreaterThan(1));
        }
    }
}
