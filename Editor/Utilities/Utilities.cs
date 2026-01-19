using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Editor.Utilities
{
    public static class ID
    {

        public static long INVALID_ID = -1;

        public static bool IsValid(long id)
        {
            return id != INVALID_ID;
        }

    }

}
