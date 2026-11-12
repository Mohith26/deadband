# Tennessee Eastman Process data (fixtures)

`d00.dat` (normal training, stored transposed 52 x 500), `d00_te.dat` (normal test) and `d06_te.dat` (fault 6, A feed loss)
are copied from the Braatz group's closed-loop TEP data set as redistributed at
https://github.com/camaramm/tennessee-eastman-profBraatz. They are used by the test suite and the live demo.

Copyright (c) 1998-2002 The Board of Trustees of the University of Illinois. All rights reserved.
Developed by: Large Scale Systems Research Laboratory, Professor Richard Braatz, Director, Department of Chemical
Engineering, University of Illinois. Redistributed under the University of Illinois/NCSA-style license in that repository.

The full set (44 files, about 25 MB) used for the benchmark is downloaded by `scripts/get-data.sh` into `data/tep-full/`.
