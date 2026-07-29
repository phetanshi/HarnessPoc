using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Web;

namespace HarnessPoc.Models
{
    public class DemoDbContext : DbContext
    {
        public DemoDbContext()
            : base("DemoDb")
        {
        }

        public DbSet<Product> Products { get; set; }
    }
}