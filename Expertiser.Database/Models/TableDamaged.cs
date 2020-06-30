using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Expertiser.Database.Models
{
    [Table("Table_Damaged")]
    public partial class TableDamaged
    {
        [Key]
        public int AccidentId { get; set; }
        [Key]
        public int CustomerId { get; set; }

        [ForeignKey(nameof(AccidentId))]
        [InverseProperty(nameof(TableAccident.TableDamaged))]
        public virtual TableAccident Accident { get; set; }
        [ForeignKey(nameof(CustomerId))]
        [InverseProperty(nameof(TableCustomer.TableDamaged))]
        public virtual TableCustomer Customer { get; set; }
    }
}
