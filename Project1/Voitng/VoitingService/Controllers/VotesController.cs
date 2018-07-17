using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;

namespace VoitingService.Controllers
{
    [Route("api/votes")]
    public class VotesController : Controller
    {
        public static long RequestCount = 0L;
        private static readonly ConcurrentDictionary<string, int> Counts = new ConcurrentDictionary<string, int>();
        private static readonly ReaderWriterLockSlim ReaderWriterLockSlim = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);

        [HttpGet]
        public IActionResult Get()
        {
            string activityId = Guid.NewGuid().ToString();
            ServiceEventSource.Current.ServiceRequestStart(nameof(VotesController.Get), activityId);

            Interlocked.Increment(ref RequestCount);

            var votes = new List<KeyValuePair<string, int>>(Counts.Count);
            foreach (var kvp in Counts)
            {
                votes.Add(kvp);
            }

            return Ok(votes);
        }

        [HttpPost("{key}")]
        public IActionResult Post([FromRoute]string key)
        {
            Interlocked.Increment(ref RequestCount);
            ReaderWriterLockSlim.EnterWriteLock();
            try
            {
                var value = Counts.GetOrAdd(key, 0);
                Counts[key] =  value + 1;
            }
            finally
            {
                ReaderWriterLockSlim.ExitWriteLock();
            }

            return NoContent();
        }
        [HttpDelete("{key}")]
        public IActionResult Delete([FromRoute]string key)
        {
            Interlocked.Increment(ref RequestCount);

            if (Counts.ContainsKey(key))
            {
                if (Counts.TryRemove(key, out var value))
                    return Ok(value);
            }

            return NotFound();
        }



    }
}