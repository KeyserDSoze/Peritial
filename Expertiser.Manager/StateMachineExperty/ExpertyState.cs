using Expertiser.Interfaces;
using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Expertiser
{
    public abstract class ExpertyState
    {
        public abstract StatusType StatusType { get; }
        public abstract Task<ExpertyState> Execute();
        public virtual async Task SendStatusChange(IEnumerable<string> emails, ExpertyState previous)
        {

        }
    }
}