using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Expertiser.Database.Models
{
    [Table("Table_InsuranceAgency")]
    public partial class TableInsuranceAgency
    {
        public TableInsuranceAgency()
        {
            TableExpertise = new HashSet<TableExpertise>();
        }

        [Key]
        public int Id { get; set; }
        [Required]
        [StringLength(100)]
        public string Label { get; set; }
        [StringLength(64)]
        public string ExternalId { get; set; }
        public int InsuranceCompanyId { get; set; }
        public int AddressId { get; set; }
        public bool IsActive { get; set; }

        [ForeignKey(nameof(AddressId))]
        [InverseProperty(nameof(TableLocation.TableInsuranceAgency))]
        public virtual TableLocation Address { get; set; }
        [ForeignKey(nameof(InsuranceCompanyId))]
        [InverseProperty(nameof(TableInsuranceCompany.TableInsuranceAgency))]
        public virtual TableInsuranceCompany InsuranceCompany { get; set; }
        [InverseProperty("InsuranceAgency")]
        public virtual ICollection<TableExpertise> TableExpertise { get; set; }
    }
}
