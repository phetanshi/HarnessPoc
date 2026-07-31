using PocForHarness.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using System.Web.Mvc;

namespace PocForHarness.Controllers
{
    public class ProductsController : Controller
    {
        private readonly DemoDbContext db = new DemoDbContext();

        public ActionResult Index()
        {
            return View(db.Products.ToList());
        }
    }
}