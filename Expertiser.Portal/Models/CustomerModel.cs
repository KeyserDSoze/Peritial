using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Expertiser.Portal.Models
{
    public class CustomerModel
    {
        public string Name { get; set; }
        public int? Id { get; set; }
        public static CustomerModel NewCustomer(int? id)
            => new CustomerModel { Id = id, Name = "CustomerId" };
        public static CustomerModel NewContractor(int? id)
            => new CustomerModel { Id = id, Name = "ContractorId" };
        public static CustomerModel NewDamaged(int? id)
            => new CustomerModel { Id = id, Name = "ContractorId" };
    }
}
