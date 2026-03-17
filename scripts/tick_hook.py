# FreeRTOS tick processing hook for Renode
# Called at every _frxt_dispatch (0x40091360)
# 'self' is the CPU object, available in hook context
bus = self.GetMachine().SystemBus
R = bus.ReadDoubleWord
W = bus.WriteDoubleWord
tc = (R(0x3FFC92F0) + 1) & 0xFFFFFFFF
W(0x3FFC92F0, tc)
nub = R(0x3FFBEDC4)
if tc >= nub:
    dl = R(0x3FFC9358)
    for _ in range(20):
        cnt = R(dl)
        if cnt == 0:
            break
        head = R(dl + 12)
        iv = R(head)
        if tc < iv:
            W(0x3FFBEDC4, iv)
            break
        # Remove head from delayed list
        nx = R(head + 4)
        pv = R(head + 8)
        W(nx + 8, pv)
        W(pv + 4, nx)
        pi = R(dl + 4)
        if pi == head:
            W(dl + 4, pv)
        W(head + 16, 0)
        W(dl, cnt - 1)
        # Remove from event list if present
        tcb = R(head + 12)
        eli = tcb + 28
        ec = R(eli + 16)
        if ec != 0:
            en = R(eli + 4)
            ep = R(eli + 8)
            W(en + 8, ep)
            W(ep + 4, en)
            ei = R(ec + 4)
            if ei == eli:
                W(ec + 4, ep)
            W(eli + 16, 0)
            W(ec, R(ec) - 1)
        # Add to ready list for this priority
        pri = R(tcb + 48)
        sli = tcb + 8
        rl = 0x3FFC9384 + pri * 20
        ri = R(rl + 4)
        rp = R(ri + 8)
        W(sli + 4, ri)
        W(sli + 8, rp)
        W(rp + 4, sli)
        W(ri + 8, sli)
        W(sli + 16, rl)
        W(rl, R(rl) + 1)
        tp = R(0x3FFC92EC)
        if pri > tp:
            W(0x3FFC92EC, pri)
    else:
        # Processed 20 items without finding end
        W(0x3FFBEDC4, 0xFFFFFFFF)
