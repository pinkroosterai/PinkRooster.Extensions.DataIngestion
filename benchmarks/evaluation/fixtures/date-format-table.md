# Custom date and time format strings

| Format specifier | Description | Examples |
|--|--|--|
| "d" | The day of the month, from 1 to 31.<br /><br /> | 2009-06-01T13:45:30 -> 1<br /><br /> 2009-06-15T13:45:30 -> 15 |
| "dd" | The day of the month, from 01 to 31.<br /><br /> | 2009-06-01T13:45:30 -> 01<br /><br /> 2009-06-15T13:45:30 -> 15 |
| "ddd" | The abbreviated name of the day of the week.<br /><br /> | 2009-06-15T13:45:30 -> Mon (en-US)<br /><br /> 2009-06-15T13:45:30 -> Пн (ru-RU)<br /><br /> 2009-06-15T13:45:30 -> lun. (fr-FR) |
| "dddd" | The full name of the day of the week.<br /><br /> | 2009-06-15T13:45:30 -> Monday (en-US)<br /><br /> 2009-06-15T13:45:30 -> понедельник (ru-RU)<br /><br /> 2009-06-15T13:45:30 -> lundi (fr-FR) |
| "f" | The tenths of a second in a date and time value.<br /><br /> | 2009-06-15T13:45:30.6170000 -> 6<br /><br /> 2009-06-15T13:45:30.05 -> 0 |
| "ff" | The hundredths of a second in a date and time value.<br /><br /> | 2009-06-15T13:45:30.6170000 -> 61<br /><br /> 2009-06-15T13:45:30.0050000 -> 00 |
| "fff" | The milliseconds in a date and time value.<br /><br /> | 6/15/2009 13:45:30.617 -> 617<br /><br /> 6/15/2009 13:45:30.0005 -> 000 |
| "ffff" | The ten thousandths of a second in a date and time value.<br /><br /> | 2009-06-15T13:45:30.6175000 -> 6175<br /><br /> 2009-06-15T13:45:30.0000500  -> 0000 |
