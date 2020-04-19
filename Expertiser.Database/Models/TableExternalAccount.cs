using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Expertiser.Database.Models
{
    [Table("Table_ExternalAccount")]
    public partial class TableExternalAccount
    {
        [Key]
        public int Id { get; set; }
        public int Type { get; set; }
        [Required]
        [StringLength(50)]
        public string Username { get; set; }
        [Required]
        [StringLength(50)]
        public string Passowrd { get; set; }
        public int MemberId { get; set; }

        [ForeignKey(nameof(MemberId))]
        [InverseProperty(nameof(TableMember.TableExternalAccount))]
        public virtual TableMember Member { get; set; }
    }
}
