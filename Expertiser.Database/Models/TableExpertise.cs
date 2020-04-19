using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Expertiser.Database.Models
{
    [Table("Table_Expertise")]
    public partial class TableExpertise
    {
        public TableExpertise()
        {
            TableBill = new HashSet<TableBill>();
            TableMessages = new HashSet<TableMessages>();
        }

        [Key]
        public int Id { get; set; }
        public int MemberId { get; set; }
        public int? ContributorId { get; set; }
        public int Status { get; set; }
        public int BranchId { get; set; }
        [Required]
        [StringLength(128)]
        public string ExternalId { get; set; }
        [Column(TypeName = "datetime")]
        public DateTime? AssignmentDate { get; set; }
        [Column(TypeName = "datetime")]
        public DateTime? InspectionDate { get; set; }
        [Column(TypeName = "decimal(18, 2)")]
        public decimal? Liquidated { get; set; }
        public int AccidentId { get; set; }

        [ForeignKey(nameof(AccidentId))]
        [InverseProperty(nameof(TableAccident.TableExpertise))]
        public virtual TableAccident Accident { get; set; }
        [ForeignKey(nameof(BranchId))]
        [InverseProperty(nameof(TableBranch.TableExpertise))]
        public virtual TableBranch Branch { get; set; }
        [ForeignKey(nameof(ContributorId))]
        [InverseProperty(nameof(TableContributor.TableExpertise))]
        public virtual TableContributor Contributor { get; set; }
        [ForeignKey(nameof(MemberId))]
        [InverseProperty(nameof(TableMember.TableExpertise))]
        public virtual TableMember Member { get; set; }
        [InverseProperty("Expertise")]
        public virtual ICollection<TableBill> TableBill { get; set; }
        [InverseProperty("Expertise")]
        public virtual ICollection<TableMessages> TableMessages { get; set; }
    }
}
