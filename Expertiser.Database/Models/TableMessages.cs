using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Expertiser.Database.Models
{
    [Table("Table_Messages")]
    public partial class TableMessages
    {
        [Key]
        public int Id { get; set; }
        public int ExpertiseId { get; set; }
        [Required]
        [StringLength(1000)]
        public string Value { get; set; }
        public int Type { get; set; }

        [ForeignKey(nameof(ExpertiseId))]
        [InverseProperty(nameof(TableExpertise.TableMessages))]
        public virtual TableExpertise Expertise { get; set; }
    }
}
