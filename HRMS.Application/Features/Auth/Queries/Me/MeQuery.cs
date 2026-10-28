using System;
using System.Collections.Generic;
using System.Text;
using MediatR;

namespace HRMS.Application.Features.Auth.Queries.Me
{
    public class MeQuery : IRequest<MeResponseDto>
    {
        public Guid UserId { get; set; }

        public MeQuery(Guid userId)
        {
            UserId = userId;
        }
    }
}
