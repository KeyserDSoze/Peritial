using Microsoft.EntityFrameworkCore;
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
            TableAccident = new HashSet<TableAccident>();
        }

        [Key]
        public int Id { get; set; }
        public int CustomerId { get; set; }
        [Required]
        [StringLength(50)]
        public string ExternalId { get; set; }
        public int InsuranceCompanyId { get; set; }
        public int? ContractorId { get; set; }
        [StringLength(50)]
        [Unicode(false)]
        public string AgencyCode { get; set; }

        [StringLength(50)]
        [Unicode(false)]
        public DateTime? StartCoverage { get; set; }

        [StringLength(50)]
        [Unicode(false)]
        public DateTime? EndCoverage { get; set; }

        [ForeignKey(nameof(ContractorId))]
        [InverseProperty(nameof(TableCustomer.TableInsuranceContractor))]
        public virtual TableCustomer Contractor { get; set; }
        [ForeignKey(nameof(CustomerId))]
        [InverseProperty(nameof(TableCustomer.TableInsuranceCustomer))]
        public virtual TableCustomer Customer { get; set; }
        [ForeignKey(nameof(InsuranceCompanyId))]
        [InverseProperty(nameof(TableInsuranceCompany.TableInsurance))]
        public virtual TableInsuranceCompany InsuranceCompany { get; set; }
        [InverseProperty("Insurance")]
        public virtual ICollection<TableAccident> TableAccident { get; set; }
    }
}
