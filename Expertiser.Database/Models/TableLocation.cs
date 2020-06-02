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
            TableAccident = new HashSet<TableAccident>();
            TableCustomer = new HashSet<TableCustomer>();
            TableInspectorate = new HashSet<TableInspectorate>();
            TableInsuranceAgency = new HashSet<TableInsuranceAgency>();
            TableInsuranceCompany = new HashSet<TableInsuranceCompany>();
        }

        [Key]
        public int Id { get; set; }
        [Required]
        [StringLength(100)]
        public string Address { get; set; }
        public int CityId { get; set; }
        [Required]
        [StringLength(10)]
        public string PostalCode { get; set; }

        [ForeignKey(nameof(CityId))]
        [InverseProperty(nameof(TableCity.TableLocation))]
        public virtual TableCity City { get; set; }
        [InverseProperty("Address")]
        public virtual ICollection<TableAccident> TableAccident { get; set; }
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
