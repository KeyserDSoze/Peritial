using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Expertiser.Database.Models
{
    [Table("Table_City")]
    public partial class TableCity
    {
        public TableCity()
        {
            TableLocation = new HashSet<TableLocation>();
        }

        [Key]
        public int Id { get; set; }
        [Required]
        [StringLength(100)]
        public string Name { get; set; }
        public int CountryId { get; set; }
        [StringLength(50)]
        public string Province { get; set; }
        [StringLength(8000)]
        public string PostalCode { get; set; }
        [StringLength(50)]
        public string Region { get; set; }

        [ForeignKey(nameof(CountryId))]
        [InverseProperty(nameof(TableCountry.TableCity))]
        public virtual TableCountry Country { get; set; }
        [InverseProperty("City")]
        public virtual ICollection<TableLocation> TableLocation { get; set; }
    }
}
