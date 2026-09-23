using Microsoft.AspNetCore.Mvc;
using Shared.Analytics;
using System.Xml.Linq;

namespace Protect_Website.Controllers;

public sealed class SitemapController : Controller
{
    [HttpGet("/sitemap.xml")]
    [ResponseCache(Duration = 3600)]
    public IActionResult Index()
    {
        XNamespace ns = "http://www.sitemaps.org/schemas/sitemap/0.9";
        var xml = new XDocument(new XElement(ns + "urlset", ProtectRouteCatalog.Routes.Select(route =>
            new XElement(ns + "url", new XElement(ns + "loc", ProtectRouteCatalog.CanonicalOrigin + route.Path)))));
        return Content(xml.ToString(), "application/xml; charset=utf-8");
    }
}
