using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Expertiser.Database.Models
{
    [Table("Table_Customer")]
    public partial class TableCustomer
    {
        public TableCustomer()
        {
            TableDamaged = new HashSet<TableDamaged>();
            TableInsuranceContractor = new HashSet<TableInsurance>();
            TableInsuranceCustomer = new HashSet<TableInsurance>();
        }

        [Key]
        public int Id { get; set; }
        [Required]
        [StringLength(100)]
        public string Name { get; set; }
        [StringLength(100)]
        public string FiscalCode { get; set; }
        public int AddressId { get; set; }

        [ForeignKey(nameof(AddressId))]
        [InverseProperty(nameof(TableLocation.TableCustomer))]
        public virtual TableLocation Address { get; set; }
        [InverseProperty("Customer")]
        public virtual ICollection<TableDamaged> TableDamaged { get; set; }
        [InverseProperty(nameof(TableInsurance.Contractor))]
        public virtual ICollection<TableInsurance> TableInsuranceContractor { get; set; }
        [InverseProperty(nameof(TableInsurance.Customer))]
        public virtual ICollection<TableInsurance> TableInsuranceCustomer { get; set; }
    }
}
