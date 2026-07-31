using System.Data.Entity;

namespace PocForHarness.Models
{
    public class DemoDbContext: DbContext
    {
        public DemoDbContext()
            : base("DemoDb")
        {
        }

        public DbSet<Product> Products { get; set; }
    }
}