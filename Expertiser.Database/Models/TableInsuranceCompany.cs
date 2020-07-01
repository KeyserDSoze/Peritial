using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Expertiser.Database.Models
{
    [Table("Table_InsuranceCompany")]
    public partial class TableInsuranceCompany
    {
        public TableInsuranceCompany()
        {
            TableInspectorate = new HashSet<TableInspectorate>();
            TableInsurance = new HashSet<TableInsurance>();
            TableInsuranceAgency = new HashSet<TableInsuranceAgency>();
        }

        [Key]
        public int Id { get; set; }
        [Required]
        [StringLength(100)]
        public string Label { get; set; }
        public int? AddressId { get; set; }
        [StringLength(64)]
        public string ExternalId { get; set; }
        public bool IsActive { get; set; }
        [StringLength(10)]
        public string Alias { get; set; }

        [ForeignKey(nameof(AddressId))]
        [InverseProperty(nameof(TableLocation.TableInsuranceCompany))]
        public virtual TableLocation Address { get; set; }
        [InverseProperty("InsuranceCompany")]
        public virtual ICollection<TableInspectorate> TableInspectorate { get; set; }
        [InverseProperty("InsuranceCompany")]
        public virtual ICollection<TableInsurance> TableInsurance { get; set; }
        [InverseProperty("InsuranceCompany")]
        public virtual ICollection<TableInsuranceAgency> TableInsuranceAgency { get; set; }
    }
}
