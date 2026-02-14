using NUnit.Framework;
using TurboHTTP.Core;

namespace TurboHTTP.Tests.Core
{
    public class ContentTypesTests
    {
        [Test]
        public void Json_IsApplicationJson()
        {
            Assert.AreEqual("application/json", ContentTypes.Json);
        }

        [Test]
        public void Xml_IsApplicationXml()
        {
            Assert.AreEqual("application/xml", ContentTypes.Xml);
        }

        [Test]
        public void FormUrlEncoded_IsCorrect()
        {
            Assert.AreEqual("application/x-www-form-urlencoded", ContentTypes.FormUrlEncoded);
        }

        [Test]
        public void MultipartFormData_IsCorrect()
        {
            Assert.AreEqual("multipart/form-data", ContentTypes.MultipartFormData);
        }

        [Test]
        public void PlainText_IsTextPlain()
        {
            Assert.AreEqual("text/plain", ContentTypes.PlainText);
        }

        [Test]
        public void OctetStream_IsCorrect()
        {
            Assert.AreEqual("application/octet-stream", ContentTypes.OctetStream);
        }

        [Test]
        public void ImageTypes_AreCorrect()
        {
            Assert.AreEqual("image/png", ContentTypes.Png);
            Assert.AreEqual("image/jpeg", ContentTypes.Jpeg);
            Assert.AreEqual("image/gif", ContentTypes.Gif);
        }

        [Test]
        public void Pdf_IsApplicationPdf()
        {
            Assert.AreEqual("application/pdf", ContentTypes.Pdf);
        }

        [Test]
        public void Zip_IsApplicationZip()
        {
            Assert.AreEqual("application/zip", ContentTypes.Zip);
        }
    }
}
