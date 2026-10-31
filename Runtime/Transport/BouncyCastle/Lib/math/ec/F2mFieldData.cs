using System;

using TurboHTTP.SecureProtocol.Org.BouncyCastle.Math.BinPoly;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Utilities;

namespace TurboHTTP.SecureProtocol.Org.BouncyCastle.Math.EC
{
    internal class F2mFieldData
    {
        internal static F2mFieldData From(int m, int k1, int k2, int k3)
        {
            return k2 == 0 ? From(m, new int[]{ k1 })
                           : From(m, new int[]{ k1, k2, k3 });
        }

        internal static F2mFieldData From(int m, int[] ks)
        {
            var mul = ks.Length == 1 ? BinPolys.Mul.Trinomial(m, ks[0])
                                     : BinPolys.Mul.Pentanomial(m, ks[0], ks[1], ks[2]);
            var inv = BinPolys.Inv.ItohTsujii(mul);
            return new F2mFieldData(m, ks, mul, inv);
        }

        internal readonly int _m;
        internal readonly int[] _ks;
        internal readonly IBinPolyMul _mul;
        internal readonly IBinPolyInv _inv;

        internal F2mFieldData(int m, int[] ks, IBinPolyMul mul, IBinPolyInv inv)
        {
            _m = m;
            _ks = ks;
            _mul = mul;
            _inv = inv;
        }

        internal int K1 => _ks[0];

        internal int K2 => _ks.Length >= 2 ? _ks[1] : 0;

        internal int K3 => _ks.Length >= 3 ? _ks[2] : 0;

        internal static bool Equals(F2mFieldData a, F2mFieldData b)
        {
            if (a == b)
                return true;

            return a._m == b._m && Arrays.AreEqual(a._ks, b._ks);
        }

        internal static int GetHashCode(F2mFieldData x) => x._m ^ Arrays.GetHashCode(x._ks);
    }
}
