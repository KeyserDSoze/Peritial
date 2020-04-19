using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Expertiser.Database.Models
{
    [Table("Table_Insurance")]
    public partial class TableInsurance
    {
        public TableInsurance()
        {
            TableExpertise = new HashSet<TableExpertise>();
        }

        [Key]
        public int Id { get; set; }
        public int CustomerId { get; set; }
        [Required]
        [StringLength(50)]
        public string ExternalId { get; set; }
        public int InsuranceCompanyId { get; set; }

        [ForeignKey(nameof(CustomerId))]
        [InverseProperty(nameof(TableCustomer.TableInsurance))]
        public virtual TableCustomer Customer { get; set; }
        [ForeignKey(nameof(InsuranceCompanyId))]
        [InverseProperty(nameof(TableInsuranceCompany.TableInsurance))]
        public virtual TableInsuranceCompany InsuranceCompany { get; set; }
        [InverseProperty("Insurance")]
        public virtual ICollection<TableExpertise> TableExpertise { get; set; }
    }
}
