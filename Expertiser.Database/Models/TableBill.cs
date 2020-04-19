using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Expertiser.Database.Models
{
    [Table("Table_Bill")]
    public partial class TableBill
    {
        [Key]
        public int Id { get; set; }
        [Column(TypeName = "decimal(18, 2)")]
        public decimal Amount { get; set; }
        [Required]
        [StringLength(100)]
        public string ExternalId { get; set; }
        [Column(TypeName = "decimal(18, 0)")]
        public decimal? Extra { get; set; }
        public int ExpertiseId { get; set; }

        [ForeignKey(nameof(ExpertiseId))]
        [InverseProperty(nameof(TableExpertise.TableBill))]
        public virtual TableExpertise Expertise { get; set; }
    }
}
