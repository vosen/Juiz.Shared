using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Vosen.MAL.Content
{
    public enum AnimelistResponse
    {
        Unknown = 0,
        Successs,
        MySQLError,
        InvalidUsername,
        ListIsPrivate,
        TooLarge
    }
}
