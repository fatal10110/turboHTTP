using System;
using System.Collections.Generic;

using TurboHTTP.SecureProtocol.Org.BouncyCastle.Asn1;
// turbo-strip: E6 SM2 curve cut; Asn1.GM and Math.EC.Custom.GM usings dropped
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Asn1.Sec;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Asn1.X9;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Math;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Math.EC;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Math.EC.Custom.Sec;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Math.EC.Endo;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Math.EC.Multiplier;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Utilities.Collections;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Utilities.Encoders;

namespace TurboHTTP.SecureProtocol.Org.BouncyCastle.Crypto.EC
{
    /// <summary>Elliptic curve registry for various customized curve implementations.</summary>
    public static class CustomNamedCurves
    {
        private static X9ECPoint ConfigureBasepoint(ECCurve curve, string encoding)
        {
            X9ECPoint G = new X9ECPoint(curve, Hex.DecodeStrict(encoding));
            WNafUtilities.ConfigureBasepoint(G.Point);
            return G;
        }

        private static ECCurve ConfigureCurve(ECCurve curve)
        {
            return curve;
        }

        private static ECCurve ConfigureCurveGlv(ECCurve c, GlvTypeBParameters p)
        {
            return c.Configure().SetEndomorphism(new GlvTypeBEndomorphism(c, p)).Create();
        }

        internal class SecP128R1Holder
            : X9ECParametersHolder
        {
            private SecP128R1Holder() {}

            internal static readonly X9ECParametersHolder Instance = new SecP128R1Holder();

            protected override ECCurve CreateCurve()
            {
                return ConfigureCurve(new SecP128R1Curve());
            }

            protected override X9ECParameters CreateParameters()
            {
                byte[] S = Hex.DecodeStrict("000E0D4D696E6768756151750CC03A4473D03679");
                ECCurve curve = Curve;
                X9ECPoint G = ConfigureBasepoint(curve,
                    "04161FF7528B899B2D0C28607CA52C5B86CF5AC8395BAFEB13C02DA292DDED7A83"); 
                return new X9ECParameters(curve, G, curve.Order, curve.Cofactor, S);
            }
        };

        internal class SecP160K1Holder
            : X9ECParametersHolder
        {
            private SecP160K1Holder() {}

            internal static readonly X9ECParametersHolder Instance = new SecP160K1Holder();

            protected override ECCurve CreateCurve()
            {
                GlvTypeBParameters glv = new GlvTypeBParameters(
                    new BigInteger("9ba48cba5ebcb9b6bd33b92830b2a2e0e192f10a", 16),
                    new BigInteger("c39c6c3b3a36d7701b9c71a1f5804ae5d0003f4", 16),
                    new ScalarSplitParameters(
                        new BigInteger[]{
                            new BigInteger("9162fbe73984472a0a9e", 16),
                            new BigInteger("-96341f1138933bc2f505", 16) },
                        new BigInteger[]{
                            new BigInteger("127971af8721782ecffa3", 16),
                            new BigInteger("9162fbe73984472a0a9e", 16) },
                        new BigInteger("9162fbe73984472a0a9d0590", 16),
                        new BigInteger("96341f1138933bc2f503fd44", 16),
                        176));
                return ConfigureCurveGlv(new SecP160K1Curve(), glv);
            }

            protected override X9ECParameters CreateParameters()
            {
                byte[] S = null;
                ECCurve curve = Curve;
                X9ECPoint G = ConfigureBasepoint(curve,
                    "043B4C382CE37AA192A4019E763036F4F5DD4D7EBB938CF935318FDCED6BC28286531733C3F03C4FEE"); 
                return new X9ECParameters(curve, G, curve.Order, curve.Cofactor, S);
            }
        };

        internal class SecP160R1Holder
            : X9ECParametersHolder
        {
            private SecP160R1Holder() {}

            internal static readonly X9ECParametersHolder Instance = new SecP160R1Holder();

            protected override ECCurve CreateCurve()
            {
                return ConfigureCurve(new SecP160R1Curve());
            }

            protected override X9ECParameters CreateParameters()
            {
                byte[] S = Hex.DecodeStrict("1053CDE42C14D696E67687561517533BF3F83345");
                ECCurve curve = Curve;
                X9ECPoint G = ConfigureBasepoint(curve,
                    "044A96B5688EF573284664698968C38BB913CBFC8223A628553168947D59DCC912042351377AC5FB32"); 
                return new X9ECParameters(curve, G, curve.Order, curve.Cofactor, S);
            }
        };

        internal class SecP160R2Holder
            : X9ECParametersHolder
        {
            private SecP160R2Holder() {}

            internal static readonly X9ECParametersHolder Instance = new SecP160R2Holder();

            protected override ECCurve CreateCurve()
            {
                return ConfigureCurve(new SecP160R2Curve());
            }

            protected override X9ECParameters CreateParameters()
            {
                byte[] S = Hex.DecodeStrict("B99B99B099B323E02709A4D696E6768756151751");
                ECCurve curve = Curve;
                X9ECPoint G = ConfigureBasepoint(curve,
                    "0452DCB034293A117E1F4FF11B30F7199D3144CE6DFEAFFEF2E331F296E071FA0DF9982CFEA7D43F2E"); 
                return new X9ECParameters(curve, G, curve.Order, curve.Cofactor, S);
            }
        };

        internal class SecP192K1Holder
            : X9ECParametersHolder
        {
            private SecP192K1Holder() {}

            internal static readonly X9ECParametersHolder Instance = new SecP192K1Holder();

            protected override ECCurve CreateCurve()
            {
                GlvTypeBParameters glv = new GlvTypeBParameters(
                    new BigInteger("bb85691939b869c1d087f601554b96b80cb4f55b35f433c2", 16),
                    new BigInteger("3d84f26c12238d7b4f3d516613c1759033b1a5800175d0b1", 16),
                    new ScalarSplitParameters(
                        new BigInteger[]{
                            new BigInteger("71169be7330b3038edb025f1", 16),
                            new BigInteger("-b3fb3400dec5c4adceb8655c", 16) },
                        new BigInteger[]{
                            new BigInteger("12511cfe811d0f4e6bc688b4d", 16),
                            new BigInteger("71169be7330b3038edb025f1", 16) },
                        new BigInteger("71169be7330b3038edb025f1d0f9", 16),
                        new BigInteger("b3fb3400dec5c4adceb8655d4c94", 16),
                        208));
                return ConfigureCurveGlv(new SecP192K1Curve(), glv);
            }

            protected override X9ECParameters CreateParameters()
            {
                byte[] S = null;
                ECCurve curve = Curve;
                X9ECPoint G = ConfigureBasepoint(curve,
                    "04DB4FF10EC057E9AE26B07D0280B7F4341DA5D1B1EAE06C7D9B2F2F6D9C5628A7844163D015BE86344082AA88D95E2F9D"); 
                return new X9ECParameters(curve, G, curve.Order, curve.Cofactor, S);
            }
        }

        internal class SecP192R1Holder
            : X9ECParametersHolder
        {
            private SecP192R1Holder() {}

            internal static readonly X9ECParametersHolder Instance = new SecP192R1Holder();

            protected override ECCurve CreateCurve()
            {
                return ConfigureCurve(new SecP192R1Curve());
            }

            protected override X9ECParameters CreateParameters()
            {
                byte[] S = Hex.DecodeStrict("3045AE6FC8422F64ED579528D38120EAE12196D5");
                ECCurve curve = Curve;
                X9ECPoint G = ConfigureBasepoint(curve,
                    "04188DA80EB03090F67CBF20EB43A18800F4FF0AFD82FF101207192B95FFC8DA78631011ED6B24CDD573F977A11E794811"); 
                return new X9ECParameters(curve, G, curve.Order, curve.Cofactor, S);
            }
        }

        internal class SecP224K1Holder
            : X9ECParametersHolder
        {
            private SecP224K1Holder() {}

            internal static readonly X9ECParametersHolder Instance = new SecP224K1Holder();

            protected override ECCurve CreateCurve()
            {
                GlvTypeBParameters glv = new GlvTypeBParameters(
                    new BigInteger("fe0e87005b4e83761908c5131d552a850b3f58b749c37cf5b84d6768", 16),
                    new BigInteger("60dcd2104c4cbc0be6eeefc2bdd610739ec34e317f9b33046c9e4788", 16),
                    new ScalarSplitParameters(
                        new BigInteger[]{
                            new BigInteger("6b8cf07d4ca75c88957d9d670591", 16),
                            new BigInteger("-b8adf1378a6eb73409fa6c9c637d", 16) },
                        new BigInteger[]{
                            new BigInteger("1243ae1b4d71613bc9f780a03690e", 16),
                            new BigInteger("6b8cf07d4ca75c88957d9d670591", 16) },
                        new BigInteger("6b8cf07d4ca75c88957d9d67059037a4", 16),
                        new BigInteger("b8adf1378a6eb73409fa6c9c637ba7f5", 16),
                        240));
                return ConfigureCurveGlv(new SecP224K1Curve(), glv);
            }

            protected override X9ECParameters CreateParameters()
            {
                byte[] S = null;
                ECCurve curve = Curve;
                X9ECPoint G = ConfigureBasepoint(curve,
                    "04A1455B334DF099DF30FC28A169A467E9E47075A90F7E650EB6B7A45C7E089FED7FBA344282CAFBD6F7E319F7C0B0BD59E2CA4BDB556D61A5"); 
                return new X9ECParameters(curve, G, curve.Order, curve.Cofactor, S);
            }
        }

        internal class SecP224R1Holder
            : X9ECParametersHolder
        {
            private SecP224R1Holder() {}

            internal static readonly X9ECParametersHolder Instance = new SecP224R1Holder();

            protected override ECCurve CreateCurve()
            {
                return ConfigureCurve(new SecP224R1Curve());
            }

            protected override X9ECParameters CreateParameters()
            {
                byte[] S = Hex.DecodeStrict("BD71344799D5C7FCDC45B59FA3B9AB8F6A948BC5");
                ECCurve curve = Curve;
                X9ECPoint G = ConfigureBasepoint(curve,
                    "04B70E0CBD6BB4BF7F321390B94A03C1D356C21122343280D6115C1D21BD376388B5F723FB4C22DFE6CD4375A05A07476444D5819985007E34"); 
                return new X9ECParameters(curve, G, curve.Order, curve.Cofactor, S);
            }
        }

        internal class SecP256K1Holder
            : X9ECParametersHolder
        {
            private SecP256K1Holder() {}

            internal static readonly X9ECParametersHolder Instance = new SecP256K1Holder();

            protected override ECCurve CreateCurve()
            {
                GlvTypeBParameters glv = new GlvTypeBParameters(
                    new BigInteger("7ae96a2b657c07106e64479eac3434e99cf0497512f58995c1396c28719501ee", 16),
                    new BigInteger("5363ad4cc05c30e0a5261c028812645a122e22ea20816678df02967c1b23bd72", 16),
                    new ScalarSplitParameters(
                        new BigInteger[]{
                            new BigInteger("3086d221a7d46bcde86c90e49284eb15", 16),
                            new BigInteger("-e4437ed6010e88286f547fa90abfe4c3", 16) },
                        new BigInteger[]{
                            new BigInteger("114ca50f7a8e2f3f657c1108d9d44cfd8", 16),
                            new BigInteger("3086d221a7d46bcde86c90e49284eb15", 16) },
                        new BigInteger("3086d221a7d46bcde86c90e49284eb153dab", 16),
                        new BigInteger("e4437ed6010e88286f547fa90abfe4c42212", 16),
                        272));
                return ConfigureCurveGlv(new SecP256K1Curve(), glv);
            }

            protected override X9ECParameters CreateParameters()
            {
                byte[] S = null;
                ECCurve curve = Curve;
                X9ECPoint G = ConfigureBasepoint(curve,
                    "0479BE667EF9DCBBAC55A06295CE870B07029BFCDB2DCE28D959F2815B16F81798483ADA7726A3C4655DA4FBFC0E1108A8FD17B448A68554199C47D08FFB10D4B8"); 
                return new X9ECParameters(curve, G, curve.Order, curve.Cofactor, S);
            }
        }

        internal class SecP256R1Holder
            : X9ECParametersHolder
        {
            private SecP256R1Holder() {}

            internal static readonly X9ECParametersHolder Instance = new SecP256R1Holder();

            protected override ECCurve CreateCurve()
            {
                return ConfigureCurve(new SecP256R1Curve());
            }

            protected override X9ECParameters CreateParameters()
            {
                byte[] S = Hex.DecodeStrict("C49D360886E704936A6678E1139D26B7819F7E90");
                ECCurve curve = Curve;
                X9ECPoint G = ConfigureBasepoint(curve,
                    "046B17D1F2E12C4247F8BCE6E563A440F277037D812DEB33A0F4A13945D898C2964FE342E2FE1A7F9B8EE7EB4A7C0F9E162BCE33576B315ECECBB6406837BF51F5"); 
                return new X9ECParameters(curve, G, curve.Order, curve.Cofactor, S);
            }
        }

        internal class SecP384R1Holder
            : X9ECParametersHolder
        {
            private SecP384R1Holder() {}

            internal static readonly X9ECParametersHolder Instance = new SecP384R1Holder();

            protected override ECCurve CreateCurve()
            {
                return ConfigureCurve(new SecP384R1Curve());
            }

            protected override X9ECParameters CreateParameters()
            {
                byte[] S = Hex.DecodeStrict("A335926AA319A27A1D00896A6773A4827ACDAC73");
                ECCurve curve = Curve;
                X9ECPoint G = ConfigureBasepoint(curve, "04"
                    + "AA87CA22BE8B05378EB1C71EF320AD746E1D3B628BA79B9859F741E082542A385502F25DBF55296C3A545E3872760AB7"
                    + "3617DE4A96262C6F5D9E98BF9292DC29F8F41DBD289A147CE9DA3113B5F0B8C00A60B1CE1D7E819D7A431D7C90EA0E5F");
                return new X9ECParameters(curve, G, curve.Order, curve.Cofactor, S);
            }
        }

        internal class SecP521R1Holder
            : X9ECParametersHolder
        {
            private SecP521R1Holder() {}

            internal static readonly X9ECParametersHolder Instance = new SecP521R1Holder();

            protected override ECCurve CreateCurve()
            {
                return ConfigureCurve(new SecP521R1Curve());
            }

            protected override X9ECParameters CreateParameters()
            {
                byte[] S = Hex.DecodeStrict("D09E8800291CB85396CC6717393284AAA0DA64BA");
                ECCurve curve = Curve;
                X9ECPoint G = ConfigureBasepoint(curve, "04"
                    + "00C6858E06B70404E9CD9E3ECB662395B4429C648139053FB521F828AF606B4D3DBAA14B5E77EFE75928FE1DC127A2FFA8DE3348B3C1856A429BF97E7E31C2E5BD66"
                    + "011839296A789A3BC0045C8A5FB42C7D1BD998F54449579B446817AFBD17273E662C97EE72995EF42640C550B9013FAD0761353C7086A272C24088BE94769FD16650");
                return new X9ECParameters(curve, G, curve.Order, curve.Cofactor, S);
            }
        }

        // turbo-strip: E6 SecT* and SM2P256V1 holders cut (deny: math/ec/custom/sec/SecT*, **/*sm2*)

        private static readonly Dictionary<string, DerObjectIdentifier> objIds =
            new Dictionary<string, DerObjectIdentifier>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<DerObjectIdentifier, X9ECParametersHolder> curves =
            new Dictionary<DerObjectIdentifier, X9ECParametersHolder>();
        private static readonly Dictionary<DerObjectIdentifier, string> names =
            new Dictionary<DerObjectIdentifier, string>();

        private static void DefineCurve(string name, DerObjectIdentifier oid, X9ECParametersHolder holder)
        {
            objIds.Add(name, oid);
            names.Add(oid, name);
            curves.Add(oid, holder);
        }

        private static void DefineCurveAlias(string name, DerObjectIdentifier oid)
        {
            if (!curves.ContainsKey(oid))
                throw new InvalidOperationException();

            objIds.Add(name, oid);
        }

        static CustomNamedCurves()
        {
            //DefineCurve("secp112r1", SecObjectIdentifiers.SecP112r1, SecP112R1Holder.Instance);
            //DefineCurve("secp112r2", SecObjectIdentifiers.SecP112r2, SecP112R2Holder.Instance);
            DefineCurve("secp128r1", SecObjectIdentifiers.SecP128r1, SecP128R1Holder.Instance);
            //DefineCurve("secp128r2", SecObjectIdentifiers.SecP128r2, SecP128R2Holder.Instance);
            DefineCurve("secp160k1", SecObjectIdentifiers.SecP160k1, SecP160K1Holder.Instance);
            DefineCurve("secp160r1", SecObjectIdentifiers.SecP160r1, SecP160R1Holder.Instance);
            DefineCurve("secp160r2", SecObjectIdentifiers.SecP160r2, SecP160R2Holder.Instance);
            DefineCurve("secp192k1", SecObjectIdentifiers.SecP192k1, SecP192K1Holder.Instance);
            DefineCurve("secp192r1", SecObjectIdentifiers.SecP192r1, SecP192R1Holder.Instance);
            DefineCurve("secp224k1", SecObjectIdentifiers.SecP224k1, SecP224K1Holder.Instance);
            DefineCurve("secp224r1", SecObjectIdentifiers.SecP224r1, SecP224R1Holder.Instance);
            DefineCurve("secp256k1", SecObjectIdentifiers.SecP256k1, SecP256K1Holder.Instance);
            DefineCurve("secp256r1", SecObjectIdentifiers.SecP256r1, SecP256R1Holder.Instance);
            DefineCurve("secp384r1", SecObjectIdentifiers.SecP384r1, SecP384R1Holder.Instance);
            DefineCurve("secp521r1", SecObjectIdentifiers.SecP521r1, SecP521R1Holder.Instance);

            // turbo-strip: E6 sect*/sm2p256v1 curves and B-/K- aliases cut (DefineCurveAlias throws on a missing OID)

            DefineCurveAlias("P-192", SecObjectIdentifiers.SecP192r1);
            DefineCurveAlias("P-224", SecObjectIdentifiers.SecP224r1);
            DefineCurveAlias("P-256", SecObjectIdentifiers.SecP256r1);
            DefineCurveAlias("P-384", SecObjectIdentifiers.SecP384r1);
            DefineCurveAlias("P-521", SecObjectIdentifiers.SecP521r1);
        }

        /// <summary>Look up the <see cref="X9ECParameters"/> for the curve with the given name.</summary>
        /// <param name="name">The name of the curve.</param>
        public static X9ECParameters GetByName(string name)
        {
            DerObjectIdentifier oid = GetOid(name);
            return oid == null ? null : GetByOid(oid);
        }

        /// <summary>Look up an <see cref="X9ECParametersHolder"/> for the curve with the given name.</summary>
        /// <remarks>
        /// Allows accessing the <see cref="ECCurve">curve</see> without necessarily triggering the creation of the
        /// full <see cref="X9ECParameters"/>.
        /// </remarks>
        /// <param name="name">The name of the curve.</param>
        public static X9ECParametersHolder GetByNameLazy(string name)
        {
            DerObjectIdentifier oid = GetOid(name);
            return oid == null ? null : GetByOidLazy(oid);
        }

        /// <summary>Look up the <see cref="X9ECParameters"/> for the curve with the given
        /// <see cref="DerObjectIdentifier">OID</see>.</summary>
        /// <param name="oid">The <see cref="DerObjectIdentifier">OID</see> for the curve.</param>
        public static X9ECParameters GetByOid(DerObjectIdentifier oid)
        {
            return GetByOidLazy(oid)?.Parameters;
        }

        /// <summary>Look up an <see cref="X9ECParametersHolder"/> for the curve with the given
        /// <see cref="DerObjectIdentifier">OID</see>.</summary>
        /// <remarks>
        /// Allows accessing the <see cref="ECCurve">curve</see> without necessarily triggering the creation of the
        /// full <see cref="X9ECParameters"/>.
        /// </remarks>
        /// <param name="oid">The <see cref="DerObjectIdentifier">OID</see> for the curve.</param>
        public static X9ECParametersHolder GetByOidLazy(DerObjectIdentifier oid)
        {
            return CollectionUtilities.GetValueOrNull(curves, oid);
        }

        /// <summary>Look up the name of the curve with the given <see cref="DerObjectIdentifier">OID</see>.</summary>
        /// <param name="oid">The <see cref="DerObjectIdentifier">OID</see> for the curve.</param>
        public static string GetName(DerObjectIdentifier oid)
        {
            return CollectionUtilities.GetValueOrNull(names, oid);
        }

        /// <summary>Look up the <see cref="DerObjectIdentifier">OID</see> of the curve with the given name.</summary>
        /// <param name="name">The name of the curve.</param>
        public static DerObjectIdentifier GetOid(string name)
        {
            return CollectionUtilities.GetValueOrNull(objIds, name);
        }

        /// <summary>Enumerate the available curve names in this registry.</summary>
        public static IEnumerable<string> Names
        {
            get { return CollectionUtilities.Proxy(objIds.Keys); }
        }
    }
}
