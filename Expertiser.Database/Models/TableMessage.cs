using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Expertiser.Database.Models
{
    [Table("Table_Message")]
    public partial class TableMessage
    {
        [Key]
        public int Id { get; set; }
        public int ExpertiseId { get; set; }
        [Required]
        [StringLength(1000)]
        public string Value { get; set; }
        [StringLength(450)]
        public string UserId { get; set; }
        public int Type { get; set; }

        [ForeignKey(nameof(ExpertiseId))]
        [InverseProperty(nameof(TableExpertise.TableMessage))]
        public virtual TableExpertise Expertise { get; set; }
        [ForeignKey(nameof(UserId))]
        [InverseProperty(nameof(AspNetUsers.TableMessage))]
        public virtual AspNetUsers User { get; set; }
    }
}
