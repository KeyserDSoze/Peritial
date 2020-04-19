using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Expertiser.Database.Models
{
    [Table("Table_Location")]
    public partial class TableLocation
    {
        public TableLocation()
        {
            TableCustomer = new HashSet<TableCustomer>();
            TableInspectorate = new HashSet<TableInspectorate>();
            TableInsuranceAgency = new HashSet<TableInsuranceAgency>();
            TableInsuranceCompany = new HashSet<TableInsuranceCompany>();
        }

        [Key]
        public int Id { get; set; }
        [StringLength(100)]
        public string Address { get; set; }
        [Required]
        [StringLength(100)]
        public string City { get; set; }
        public int CountryId { get; set; }
        [StringLength(16)]
        public string PostalCode { get; set; }

        [ForeignKey(nameof(CountryId))]
        [InverseProperty(nameof(TableCountry.TableLocation))]
        public virtual TableCountry Country { get; set; }
        [InverseProperty("Address")]
        public virtual ICollection<TableCustomer> TableCustomer { get; set; }
        [InverseProperty("Address")]
        public virtual ICollection<TableInspectorate> TableInspectorate { get; set; }
        [InverseProperty("Address")]
        public virtual ICollection<TableInsuranceAgency> TableInsuranceAgency { get; set; }
        [InverseProperty("Address")]
        public virtual ICollection<TableInsuranceCompany> TableInsuranceCompany { get; set; }
    }
}
